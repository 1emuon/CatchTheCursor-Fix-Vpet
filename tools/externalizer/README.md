# externalizer — Harmony 外置化 / 引用重写工具

本工具是 CatchTheCursor-Fix-Vpet 修复流程的第一步：把单文件插件 DLL 中**内嵌的 Harmony 生态类型**剥离出来，并将所有类型/方法/字段引用重写为指向外部 `0Harmony` 程序集，为升级 Harmony 版本做准备。

## 原理

社区修复版 `CatchTheMousev1.10.dll` 是用 ILRepack 合并的单文件程序集，内嵌了 HarmonyLib / MonoMod / Iced / Mono.Cecil 等约 2442 个类型。工具基于 Mono.Cecil：

1. 按命名空间前缀（`HarmonyLib`、`MonoMod`、`Iced`、`Mono.Cecil`、`Mono.CompilerServices`、`Microsoft.Cci`、`System.Text.Json`）匹配并移除内嵌类型，**同时把被移除类型的全部嵌套类型纳入清理集**（嵌套类型的命名空间常为空，前缀匹配会漏掉，如 `MonoMod.Core.Interop.CoreCLR/CorJitResult`）；
2. 重写保留代码中对被移除类型的引用：优先指向外部 `0Harmony.dll` 中同名类型，框架类型（如 `System.Text.Json`）回退到框架程序集引用；
3. **悬挂引用扫描（sweep）**：全量遍历保留类型的签名、方法体、自定义特性，找出仍引用已删除 `TypeDefinition` 的类型（典型如编译器生成的 `<>f__AnonymousDelegate*`，其签名引用了被删的 MonoMod JIT 互操作结构体）。若不做这一步，Cecil 写出时会把这些引用写成越界 TypeDef token，导致后续工具（如 ILRepack）急切读取签名时崩溃。若仍被保留代码引用则报错拒绝删除，避免误伤；
4. 清理 ILRepack 遗留的模块初始化桩与 `ExportedTypes` 转发条目。

## 依赖与编译

- 任意 C# 编译器（csc / dotnet build 均可）
- [Mono.Cecil](https://www.nuget.org/packages/Mono.Cecil)（任意近期版本，编译时引用 `Mono.Cecil.dll`）

```bash
csc /nologo /t:exe /out:Ext.exe /r:Mono.Cecil.dll Program.cs
```

## 用法

```bash
# 外置化：剥离内嵌 Harmony，引用重写为外部 0Harmony
Ext.exe patch <原始插件.dll> <新版0Harmony.dll> <输出.dll> [额外程序集解析目录...]

# 校验：确认无内嵌类型残留、无不可解析引用（scope 为本程序集或 0Harmony）
Ext.exe verify <输出.dll> [解析目录...]

# 诊断：急切读取全部方法签名（复现 ILRepack 等工具的解析崩溃）
Ext.exe readall <dll> [解析目录...]

# 诊断：打印指定类型的签名引用对象形态
Ext.exe inspect <dll> <类型全名> [解析目录...]
```

解析目录用于让 Cecil 解析插件引用的游戏程序集（如 VPet-Simulator 各 DLL），传游戏安装目录即可。

## 完整修复流水线

```bash
# 1. 外置化（本工具）
Ext.exe patch CatchTheMousev1.10.bak.dll 0Harmony.2.4.2.dll ext.dll "C:\Path\To\VPet"

# 2. 用 ILRepack 把新版 Harmony 合并回单文件
ILRepack.exe /union /out:CatchTheMousev1.10.dll ext.dll 0Harmony.2.4.2.dll /lib:"C:\Path\To\VPet"

# 3. 验证产物可完整解析
Ext.exe readall CatchTheMousev1.10.dll "C:\Path\To\VPet"
```

其中 `0Harmony.2.4.2.dll` 取自 NuGet 包 [Lib.Harmony 2.4.2](https://www.nuget.org/packages/Lib.Harmony/2.4.2) 的 `lib/net10.0/` 目录；ILRepack 取自 NuGet 包 [ILRepack](https://www.nuget.org/packages/ILRepack)。
