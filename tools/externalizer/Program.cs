using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// HarmonyExternalizer
//   patch  : HarmonyExternalizer.exe patch <plugin.dll> <0Harmony.dll> <out.dll>
//   verify : HarmonyExternalizer.exe verify <patched.dll>
class Program
{
    static readonly string[] MergedRoots =
    {
        "HarmonyLib", "MonoMod", "Iced", "Mono.Cecil",
        "Mono.CompilerServices", "Microsoft.Cci", "System.Text.Json"
    };

    static ModuleDefinition plugin = null!;
    static ModuleDefinition harmony = null!;
    static Dictionary<string, TypeDefinition> harmonyTypes = null!;
    static readonly HashSet<string> missing = new HashSet<string>();
    static int rewrittenTypeRefs, rewrittenMethodRefs, rewrittenFieldRefs;

    static bool IsMergedNs(string ns) =>
        MergedRoots.Any(r => ns == r || ns.StartsWith(r + "."));

    static TypeDefinition? SafeResolve(TypeReference tr)
    {
        try { return tr.Resolve(); } catch { return null; }
    }

    static MethodDefinition? SafeResolve(MethodReference mr)
    {
        try { return mr.Resolve(); } catch { return null; }
    }

    static FieldDefinition? SafeResolve(FieldReference fr)
    {
        try { return fr.Resolve(); } catch { return null; }
    }

    static TypeReference? FrameworkFallback(TypeDefinition def)
    {
        string ns = def.Namespace ?? "";
        if (ns == "System.Text.Json" || ns.StartsWith("System.Text.Json."))
        {
            var anr = plugin.AssemblyReferences.FirstOrDefault(a => a.Name == "System.Text.Json");
            if (anr == null)
            {
                anr = new AssemblyNameReference("System.Text.Json", new Version(10, 0, 0, 0));
                plugin.AssemblyReferences.Add(anr);
            }
            TypeReference? decl = def.DeclaringType != null ? FrameworkFallback(def.DeclaringType) : null;
            return new TypeReference(ns, def.Name, plugin, anr) { DeclaringType = decl };
        }
        return null;
    }

    static TypeReference ImportMerged(TypeDefinition def)
    {
        if (harmonyTypes.TryGetValue(def.FullName, out var ext))
            return plugin.ImportReference(ext);
        var fb = FrameworkFallback(def);
        if (fb != null)
        {
            Console.WriteLine("[i] framework fallback: " + def.FullName);
            return fb;
        }
        // generated-name alias: MonoMod.<oldguid>MMDbgLog -> MonoMod.<newguid>MMDbgLog
        var norm = NormName(def.FullName);
        foreach (var kv in harmonyTypes)
        {
            if (NormName(kv.Key) == norm)
            {
                Console.WriteLine("[i] generated-name alias: " + def.FullName + " -> " + kv.Key);
                return plugin.ImportReference(kv.Value);
            }
        }
        missing.Add(def.FullName);
        throw new Exception("merged type has no external home: " + def.FullName);
    }

    static string NormName(string fullName)
    {
        // replace "<...>" name decorations (build-unique) with "<>" so generated types match across builds
        var sb = new System.Text.StringBuilder();
        bool inAngle = false;
        foreach (var c in fullName)
        {
            if (c == '<') inAngle = true;
            if (inAngle)
            {
                if (c == '>') { sb.Append("<>"); inAngle = false; }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    static TypeReference MapType(TypeReference? tr)
    {
        if (tr == null) return null!;
        if (tr is GenericParameter) return tr;

        if (tr is ArrayType arr)
        {
            var e = MapType(arr.ElementType);
            return ReferenceEquals(e, arr.ElementType) ? tr : new ArrayType(e, arr.Rank);
        }
        if (tr is ByReferenceType br)
        {
            var e = MapType(br.ElementType);
            return ReferenceEquals(e, br.ElementType) ? tr : new ByReferenceType(e);
        }
        if (tr is PointerType pt)
        {
            var e = MapType(pt.ElementType);
            return ReferenceEquals(e, pt.ElementType) ? tr : new PointerType(e);
        }
        if (tr is PinnedType pin)
        {
            var e = MapType(pin.ElementType);
            return ReferenceEquals(e, pin.ElementType) ? tr : new PinnedType(e);
        }
        if (tr is SentinelType sen)
        {
            var e = MapType(sen.ElementType);
            return ReferenceEquals(e, sen.ElementType) ? tr : new SentinelType(e);
        }
        if (tr is OptionalModifierType om)
            return new OptionalModifierType(MapType(om.ModifierType), MapType(om.ElementType));
        if (tr is RequiredModifierType rm)
            return new RequiredModifierType(MapType(rm.ModifierType), MapType(rm.ElementType));
        if (tr is GenericInstanceType git)
        {
            var elem = MapType(git.ElementType);
            var ng = new GenericInstanceType(elem);
            foreach (var a in git.GenericArguments)
                ng.GenericArguments.Add(MapType(a));
            rewrittenTypeRefs++;
            return ng;
        }
        if (tr is FunctionPointerType fp)
        {
            var nf = new FunctionPointerType
            {
                HasThis = fp.HasThis,
                ExplicitThis = fp.ExplicitThis,
                CallingConvention = fp.CallingConvention,
                ReturnType = MapType(fp.ReturnType)
            };
            foreach (var p in fp.Parameters)
                nf.Parameters.Add(CopyParameter(p));
            return nf;
        }

        var def = SafeResolve(tr);
        if (def != null && IsMergedNs(def.Namespace ?? ""))
        {
            rewrittenTypeRefs++;
            return ImportMerged(def);
        }
        return tr;
    }

    static ParameterDefinition CopyParameter(ParameterDefinition p)
    {
        var np = new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType));
        if (p.HasConstant) np.Constant = p.Constant;
        np.MarshalInfo = RemapMarshal(p.MarshalInfo);
        CopyAttrs(np.CustomAttributes);
        return np;
    }

    static MarshalInfo? RemapMarshal(MarshalInfo? mi)
    {
        if (mi is CustomMarshalInfo cmt && cmt.ManagedType != null)
            cmt.ManagedType = MapType(cmt.ManagedType);
        return mi;
    }

    static void CopyAttrs(Mono.Collections.Generic.Collection<CustomAttribute> attrs)
    {
        var old = attrs.ToList();
        attrs.Clear();
        foreach (var ca in old)
        {
            var nca = new CustomAttribute(MapMethod(ca.Constructor));
            foreach (var a in ca.ConstructorArguments)
                nca.ConstructorArguments.Add(MapAttrArg(a));
            foreach (var f in ca.Fields)
                nca.Fields.Add(new CustomAttributeNamedArgument(f.Name, MapAttrArg(f.Argument)));
            foreach (var p in ca.Properties)
                nca.Properties.Add(new CustomAttributeNamedArgument(p.Name, MapAttrArg(p.Argument)));
            attrs.Add(nca);
        }
    }

    static CustomAttributeArgument MapAttrArg(CustomAttributeArgument a)
    {
        var t = MapType(a.Type);
        object? v = a.Value;
        if (v is TypeReference vtr) v = MapType(vtr);
        else if (v is CustomAttributeArgument caa) v = MapAttrArg(caa);
        else if (v is CustomAttributeArgument[] arr) v = arr.Select(MapAttrArg).ToArray();
        return new CustomAttributeArgument(t, v!);
    }

    static MethodDefinition FindMethod(TypeDefinition extDecl, MethodReference mr)
    {
        var cands = extDecl.Methods.Where(m =>
            m.Name == mr.Name && m.Parameters.Count == mr.Parameters.Count &&
            m.GenericParameters.Count == mr.GenericParameters.Count).ToList();
        if (cands.Count > 1)
        {
            var sig = string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));
            cands = cands.Where(m =>
                string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) == sig).ToList();
        }
        if (cands.Count == 0) throw new Exception("external method not found: " + mr.FullName);
        if (cands.Count > 1) Console.WriteLine("[w] ambiguous: " + mr.FullName + " picking first");
        return cands[0];
    }

    static MethodReference MapMethod(MethodReference? mr)
    {
        if (mr == null) return null!;
        if (mr is GenericInstanceMethod gim)
        {
            var elem = MapMethod(gim.ElementMethod);
            var ng = new GenericInstanceMethod(elem);
            foreach (var a in gim.GenericArguments)
                ng.GenericArguments.Add(MapType(a));
            rewrittenMethodRefs++;
            return ng;
        }

        var declDef = SafeResolve(mr.DeclaringType);
        if (declDef != null && IsMergedNs(declDef.Namespace ?? ""))
        {
            if (!harmonyTypes.TryGetValue(declDef.FullName, out var extDecl))
                throw new Exception("merged decl has no external home: " + declDef.FullName);
            var ext = FindMethod(extDecl, mr);
            rewrittenMethodRefs++;
            return plugin.ImportReference(ext);
        }

        if (mr is MethodDefinition md && md.Module == plugin)
            return mr; // own method; signature rewritten at definition site

        // memberref on non-merged decl: may still carry merged types in decl args or signature
        var newDecl = MapType(mr.DeclaringType);
        bool sigHasMerged = mr.Parameters.Any(p =>
        {
            var d = SafeResolve(p.ParameterType);
            return d != null && IsMergedNs(d.Namespace ?? "");
        });
        var rt = SafeResolve(mr.ReturnType);
        bool retMerged = rt != null && IsMergedNs(rt.Namespace ?? "");
        if (!ReferenceEquals(newDecl, mr.DeclaringType) || sigHasMerged || retMerged)
        {
            var nm = new MethodReference(mr.Name, MapType(mr.ReturnType), newDecl)
            {
                HasThis = mr.HasThis,
                ExplicitThis = mr.ExplicitThis,
                CallingConvention = mr.CallingConvention
            };
            foreach (var p in mr.Parameters)
                nm.Parameters.Add(CopyParameter(p));
            foreach (var gp in mr.GenericParameters)
                nm.GenericParameters.Add(new GenericParameter(gp.Name, nm) { Attributes = gp.Attributes });
            rewrittenMethodRefs++;
            return nm;
        }
        return mr;
    }

    static FieldReference MapField(FieldReference? fr)
    {
        if (fr == null) return null!;
        var declDef = SafeResolve(fr.DeclaringType);
        if (declDef != null && IsMergedNs(declDef.Namespace ?? ""))
        {
            if (!harmonyTypes.TryGetValue(declDef.FullName, out var extDecl))
                throw new Exception("merged decl has no external home: " + declDef.FullName);
            var cands = extDecl.Fields.Where(f => f.Name == fr.Name).ToList();
            if (cands.Count == 0) throw new Exception("external field not found: " + fr.FullName);
            rewrittenFieldRefs++;
            return plugin.ImportReference(cands[0]);
        }
        if (fr is FieldDefinition fd && fd.Module == plugin)
            return fr;
        var ft = SafeResolve(fr.FieldType);
        if (ft != null && IsMergedNs(ft.Namespace ?? ""))
        {
            rewrittenFieldRefs++;
            return new FieldReference(fr.Name, MapType(fr.FieldType), MapType(fr.DeclaringType));
        }
        return fr;
    }

    static void RewriteBody(MethodBody? body)
    {
        if (body == null) return;
        foreach (var v in body.Variables)
            v.VariableType = MapType(v.VariableType);
        foreach (var eh in body.ExceptionHandlers)
            eh.CatchType = MapType(eh.CatchType);
        foreach (var ins in body.Instructions)
        {
            switch (ins.Operand)
            {
                case TypeReference t: ins.Operand = MapType(t); break;
                case MethodReference m: ins.Operand = MapMethod(m); break;
                case FieldReference f: ins.Operand = MapField(f); break;
                case CallSite cs:
                    var ncs = new CallSite(MapType(cs.ReturnType))
                    {
                        HasThis = cs.HasThis,
                        ExplicitThis = cs.ExplicitThis,
                        CallingConvention = cs.CallingConvention
                    };
                    foreach (var p in cs.Parameters) ncs.Parameters.Add(CopyParameter(p));
                    ins.Operand = ncs;
                    break;
            }
        }
    }

    static void RewriteMethod(MethodDefinition m)
    {
        foreach (var gp in m.GenericParameters)
        {
            for (int i = 0; i < gp.Constraints.Count; i++)
                gp.Constraints[i].ConstraintType = MapType(gp.Constraints[i].ConstraintType);
            CopyAttrs(gp.CustomAttributes);
        }
        m.MethodReturnType.ReturnType = MapType(m.MethodReturnType.ReturnType);
        m.MethodReturnType.MarshalInfo = RemapMarshal(m.MethodReturnType.MarshalInfo);
        CopyAttrs(m.MethodReturnType.CustomAttributes);
        foreach (var p in m.Parameters)
        {
            p.ParameterType = MapType(p.ParameterType);
            p.MarshalInfo = RemapMarshal(p.MarshalInfo);
            CopyAttrs(p.CustomAttributes);
        }
        CopyAttrs(m.CustomAttributes);
        RewriteBody(m.Body);
    }

    static void RewriteType(TypeDefinition t)
    {
        // ILRepack adds <Module> stubs like "0Harmony_Version_..._ModuleInitializer" that call
        // into the merged assemblies; with external 0Harmony these must go away entirely
        if (t.Name == "<Module>")
        {
            var inits = t.Methods.Where(m => m.Name.Contains("_ModuleInitializer")).ToList();
            if (inits.Count > 0)
            {
                Console.WriteLine($"[i] removing {inits.Count} ILRepack module-initializer stubs");
                foreach (var m in t.Methods.Where(x => x.HasBody).ToList())
                {
                    var dead = m.Body.Instructions
                        .Where(i => i.Operand is MethodReference mr && mr.Name.Contains("_ModuleInitializer"))
                        .ToList();
                    foreach (var d in dead)
                        m.Body.Instructions.Remove(d);
                }
                foreach (var m in inits)
                    t.Methods.Remove(m);
            }
        }
        t.BaseType = MapType(t.BaseType);
        foreach (var ii in t.Interfaces)
            ii.InterfaceType = MapType(ii.InterfaceType);
        foreach (var gp in t.GenericParameters)
        {
            for (int i = 0; i < gp.Constraints.Count; i++)
                gp.Constraints[i].ConstraintType = MapType(gp.Constraints[i].ConstraintType);
            CopyAttrs(gp.CustomAttributes);
        }
        foreach (var f in t.Fields)
        {
            f.FieldType = MapType(f.FieldType);
            f.MarshalInfo = RemapMarshal(f.MarshalInfo);
            CopyAttrs(f.CustomAttributes);
        }
        foreach (var m in t.Methods)
        {
            try
            {
                RewriteMethod(m);
            }
            catch (Exception)
            {
                Console.WriteLine("[!] while rewriting method: " + m.FullName);
                throw;
            }
        }
        foreach (var p in t.Properties)
        {
            p.PropertyType = MapType(p.PropertyType);
            CopyAttrs(p.CustomAttributes);
        }
        foreach (var e in t.Events)
        {
            e.EventType = MapType(e.EventType);
            CopyAttrs(e.CustomAttributes);
        }
        CopyAttrs(t.CustomAttributes);
        foreach (var nt in t.NestedTypes.ToList())
            if (IsMergedNs(nt.Namespace ?? ""))
                t.NestedTypes.Remove(nt);
            else
                RewriteType(nt);
    }

    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "inspect")
        {
            var m = ReadWithResolver(args[1], args.Skip(3));
            foreach (var t in m.GetTypes())
            {
                if (t.FullName != args[2]) continue;
                void Dump(string what, TypeReference tr)
                {
                    if (tr == null) { Console.WriteLine($"  {what}: <null>"); return; }
                    Console.WriteLine($"  {what}: {tr.GetType().Name} def={tr.IsDefinition} tok={tr.MetadataToken.TokenType}/{tr.MetadataToken.ToInt32():X} scope={(tr.Scope == null ? "<null>" : tr.Scope.Name)} full={tr.FullName}");
                    if (tr is TypeSpecification ts) Dump("  elem", ts.ElementType);
                }
                Console.WriteLine("type " + t.FullName);
                Dump("base", t.BaseType);
                foreach (var mm in t.Methods)
                {
                    Console.WriteLine(" method " + mm.Name);
                    Dump("  ret", mm.ReturnType);
                    foreach (var p in mm.Parameters) Dump("  param " + p.Name, p.ParameterType);
                }
            }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "readall")
        {
            // eagerly read every type/method/signature to reproduce parser failures
            var m = ReadWithResolver(args[1], args.Skip(2));
            int n = 0;
            foreach (var t in m.GetTypes())
            {
                string tn = t.FullName;
                try
                {
                    var fs = t.Fields.Count;
                    foreach (var mm in t.Methods)
                    {
                        var sig = mm.FullName; // force signature read
                        if (mm.HasBody)
                            foreach (var i in mm.Body.Instructions) { var o = i.Operand; }
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] FAIL type: {tn}");
                    Console.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
            Console.WriteLine($"[+] readall done, {n} methods read OK");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "verify")
            return Verify(args[1], args.Skip(2));
        if (args.Length < 4 || args[0] != "patch")
        {
            Console.WriteLine("usage: patch <plugin.dll> <0Harmony.dll> <out.dll> | verify <patched.dll>");
            return 2;
        }

        plugin = ReadWithResolver(args[1], args.Skip(4));
        harmony = ReadWithResolver(args[2], args.Skip(4));

        harmonyTypes = new Dictionary<string, TypeDefinition>();
        foreach (var t in harmony.GetTypes())
            harmonyTypes[t.FullName] = t;
        Console.WriteLine($"[i] harmony module types: {harmonyTypes.Count}");

        var allTypes = plugin.GetTypes().ToList();
        var merged = allTypes.Where(t => IsMergedNs(t.Namespace ?? "")).ToHashSet();
        Console.WriteLine($"[i] merged types to remove: {merged.Count} / {allTypes.Count}");

        // removedSet = merged types plus everything nested inside them
        // (nested types often carry an empty namespace and never match IsMergedNs)
        var removedSet = new HashSet<TypeDefinition>(merged);
        void MarkNested(TypeDefinition td)
        {
            foreach (var n in td.NestedTypes)
            {
                removedSet.Add(n);
                MarkNested(n);
            }
        }
        foreach (var t in merged.ToList())
            MarkNested(t);
        Console.WriteLine($"[i] total types detached (incl. nested): {removedSet.Count}");

        var keep = allTypes.Where(t => !merged.Contains(t) && t.DeclaringType == null).ToList();
        foreach (var t in keep)
        {
            try
            {
                RewriteType(t);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] while rewriting type: " + t.FullName);
                throw;
            }
        }

        if (plugin.Assembly != null)
            CopyAttrs(plugin.Assembly.CustomAttributes);
        CopyAttrs(plugin.CustomAttributes);

        foreach (var et in plugin.ExportedTypes.ToList())
            if (IsMergedNs(et.Namespace ?? ""))
                plugin.ExportedTypes.Remove(et);

        foreach (var t in merged.Where(x => x.DeclaringType == null).ToList())
            plugin.Types.Remove(t);

        // sweep: kept types whose signatures still touch removed TypeDefinitions
        // would be written as dangling TypeDef tokens (out-of-range rid) and crash
        // eager readers (ILRepack's Cecil). Remove them if nothing kept references them.
        while (true)
        {
            var cands = plugin.GetTypes()
                .Where(t => !removedSet.Contains(t))
                .Where(t => WalkRefs(t, tr => tr is TypeDefinition x && removedSet.Contains(x)))
                .ToList();
            if (cands.Count == 0) break;
            var candSet = new HashSet<TypeDefinition>(cands);
            foreach (var c in cands)
            {
                var refs = plugin.GetTypes()
                    .Where(u => !candSet.Contains(u) && !removedSet.Contains(u))
                    .Where(u => WalkRefs(u, tr => UnwrapToDef(tr) == c))
                    .Select(u => u.FullName).ToList();
                if (refs.Count > 0)
                    throw new Exception($"cannot sweep dangling type {c.FullName}: still referenced by {string.Join(", ", refs)}");
            }
            foreach (var c in cands)
            {
                if (c.DeclaringType != null) c.DeclaringType.NestedTypes.Remove(c);
                else plugin.Types.Remove(c);
                removedSet.Add(c);
                Console.WriteLine("[i] swept dangling type: " + c.FullName);
            }
        }

        Console.WriteLine($"[i] rewritten typerefs: {rewrittenTypeRefs}, methodrefs: {rewrittenMethodRefs}, fieldrefs: {rewrittenFieldRefs}");
        if (missing.Count > 0)
        {
            Console.WriteLine("[!] missing external homes:");
            foreach (var m in missing) Console.WriteLine("    " + m);
        }

        plugin.Write(args[3]);
        Console.WriteLine("[+] written: " + args[3]);
        return 0;
    }

    static ModuleDefinition ReadWithResolver(string path, IEnumerable<string> extraDirs)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        foreach (var d in extraDirs)
            resolver.AddSearchDirectory(d);
        return ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
    }

    static readonly string[] VpetSidePrefixes =
    {
        "VPet", "LinePutScript", "Panuon", "Facepunch", "Steamworks", "NAudio",
        "WpfAnimatedGif", "SkiaSharp", "HKW", "CommunityToolkit", "Microsoft.CSharp",
        "Microsoft.VisualBasic", "Microsoft.Win32", "Microsoft.Xaml", "Microsoft.Extensions",
        "Microsoft.DiaSymReader", "System.Drawing", "System.Windows", "System.Security",
        "System.Configuration", "Accessibility"
    };

    static int Verify(string path, IEnumerable<string> extraDirs)
    {
        var asm = ReadWithResolver(path, extraDirs);
        int bad = 0, mergedLeft = 0;
        var typedefs = asm.GetTypes();
        foreach (var t in typedefs)
            if (IsMergedNs(t.Namespace ?? ""))
            {
                mergedLeft++;
                Console.WriteLine("[!] merged typedef left: " + t.FullName);
            }
        Console.WriteLine($"[i] typedefs: {typedefs.Count()}, merged left: {mergedLeft}");

        foreach (var tr in asm.GetTypeReferences())
        {
            var scope = tr.Scope.Name;
            if (scope != asm.Assembly.Name.Name && scope != "0Harmony") continue;
            if (SafeResolve(tr) == null)
            {
                Console.WriteLine("[!] unresolved typeref: " + tr.FullName + " (scope " + scope + ")");
                bad++;
            }
        }
        foreach (var mr in asm.GetMemberReferences())
        {
            var scope = mr.DeclaringType.Scope.Name;
            if (scope != asm.Assembly.Name.Name && scope != "0Harmony") continue;
            if (SafeResolve(mr as MethodReference) == null && SafeResolve(mr as FieldReference) == null)
            {
                Console.WriteLine("[!] unresolved memberref: " + mr.FullName + " (scope " + scope + ")");
                bad++;
            }
        }
        Console.WriteLine(bad == 0 && mergedLeft == 0 ? "[+] VERIFY OK" : "[!] VERIFY FAILED: " + bad + " problems");
        return bad == 0 && mergedLeft == 0 ? 0 : 1;
    }

    static TypeReference? UnwrapToDef(TypeReference tr)
    {
        while (tr is TypeSpecification ts)
            tr = ts.ElementType;
        return tr;
    }

    // Walk every type/method/field reference reachable from a type definition
    // (signatures, bodies, custom attributes, nested types) and apply pred.
    static bool WalkRefs(TypeDefinition t, Func<TypeReference, bool> pred)
    {
        bool ChkT(TypeReference? tr)
        {
            if (tr == null) return false;
            if (pred(tr)) return true;
            if (tr is TypeSpecification ts)
            {
                if (ChkT(ts.ElementType)) return true;
                if (tr is GenericInstanceType gi)
                    foreach (var a in gi.GenericArguments)
                        if (ChkT(a)) return true;
            }
            return false;
        }
        bool ChkM(MethodReference? mr)
        {
            if (mr == null) return false;
            if (ChkT(mr.DeclaringType)) return true;
            if (ChkT(mr.ReturnType)) return true;
            foreach (var p in mr.Parameters)
                if (ChkT(p.ParameterType)) return true;
            if (mr is GenericInstanceMethod gim)
                foreach (var a in gim.GenericArguments)
                    if (ChkT(a)) return true;
            if (mr is MethodSpecification msp && !ReferenceEquals(msp.ElementMethod, mr))
                return ChkM(msp.ElementMethod);
            return false;
        }
        bool ChkF(FieldReference? fr) =>
            fr != null && (ChkT(fr.DeclaringType) || ChkT(fr.FieldType));

        bool ChkCA(CustomAttribute? ca)
        {
            if (ca == null) return false;
            if (ChkT(ca.AttributeType) || ChkM(ca.Constructor)) return true;
            bool ChkVal(object? v)
            {
                if (v is TypeReference vtr) return ChkT(vtr);
                if (v is CustomAttributeArgument na) return ChkVal(na.Value);
                if (v is CustomAttributeArgument[] arr)
                    foreach (var x in arr)
                        if (ChkVal(x.Value)) return true;
                return false;
            }
            foreach (var a in ca.ConstructorArguments)
                if (ChkVal(a.Value)) return true;
            foreach (var a in ca.Fields)
                if (ChkVal(a.Argument.Value)) return true;
            foreach (var a in ca.Properties)
                if (ChkVal(a.Argument.Value)) return true;
            return false;
        }

        bool ChkGP(GenericParameter gp)
        {
            foreach (var c in gp.Constraints)
                if (ChkT(c.ConstraintType)) return true;
            foreach (var ca in gp.CustomAttributes)
                if (ChkCA(ca)) return true;
            return false;
        }

        if (ChkT(t.BaseType)) return true;
        foreach (var i in t.Interfaces)
            if (ChkT(i.InterfaceType) || ChkCA(i.CustomAttributes.FirstOrDefault())) return true;
        foreach (var gp in t.GenericParameters)
            if (ChkGP(gp)) return true;
        foreach (var ca in t.CustomAttributes)
            if (ChkCA(ca)) return true;
        foreach (var f in t.Fields)
        {
            if (ChkT(f.FieldType)) return true;
            foreach (var ca in f.CustomAttributes)
                if (ChkCA(ca)) return true;
        }
        foreach (var p in t.Properties)
        {
            if (ChkT(p.PropertyType)) return true;
            foreach (var ca in p.CustomAttributes)
                if (ChkCA(ca)) return true;
        }
        foreach (var e in t.Events)
        {
            if (ChkT(e.EventType)) return true;
            foreach (var ca in e.CustomAttributes)
                if (ChkCA(ca)) return true;
        }
        foreach (var m in t.Methods)
        {
            if (ChkM(m)) return true;
            if (m.HasOverrides)
                foreach (var ov in m.Overrides)
                    if (ChkM(ov)) return true;
            foreach (var gp in m.GenericParameters)
                if (ChkGP(gp)) return true;
            foreach (var ca in m.CustomAttributes)
                if (ChkCA(ca)) return true;
            if (m.HasBody)
            {
                foreach (var v in m.Body.Variables)
                    if (ChkT(v.VariableType)) return true;
                foreach (var eh in m.Body.ExceptionHandlers)
                    if (ChkT(eh.CatchType)) return true;
                foreach (var ins in m.Body.Instructions)
                {
                    var op = ins.Operand;
                    if (op is MethodReference omr && ChkM(omr)) return true;
                    if (op is FieldReference ofr && ChkF(ofr)) return true;
                    if (op is TypeReference otr && ChkT(otr)) return true;
                }
            }
        }
        foreach (var nt in t.NestedTypes)
            if (WalkRefs(nt, pred)) return true;
        return false;
    }
}
