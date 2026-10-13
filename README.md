# CatchTheCursor-Fix-Vpet

VPet「抓光标」(CatchTheCursor) 插件的 .NET 10 兼容修复版。
A .NET 10 compatibility fix for the VPet plugin "CatchTheCursor" (抓光标).

---

## 中文

### 这是什么

《虚拟桌宠模拟器 (VPet)》的 MOD「抓光标」的修复文件，该mod在某次更新后添加枕头好睡的修复dll后会报以下错误，同时介于在游戏启动时会进行mod校验，本人通过AI进行了修复。

```
HarmonyLib.HarmonyException
 ---> System.PlatformNotSupportedException: CoreCLR version 10.0.12 is not supported
```

原因是插件 DLL 内嵌的 Harmony 2.4.1 补丁库不支持 .NET 10。本仓库将其升级到 **Harmony 2.4.2** 并重新合并为单文件，插件功能逻辑与原 MOD 完全一致，业务代码零改动。

### 安装方法（从创意工坊安装的请查看方法二）

**前置条件：完全退出 VPet 游戏，并退订 Steam 创意工坊中的「抓光标」MOD。**

#### 方式一：已有本地 mod 目录（推荐，最简）

下载 [Releases](../../releases) 中的 `CatchTheMousev1.10.dll`，覆盖到：

```
<游戏安装目录>\mod\CatchTheCursorGame\plugin\CatchTheMousev1.10.dll
```

#### 方式二：从创意工坊版转换（保留原 mod 文件）

1. 完全退出游戏，并在 Steam 中退订工坊版「抓光标」。
2. 找到工坊目录：`steamapps\workshop\content\<VPet 的 AppID>\3210888681\`。
3. 把整个文件夹**复制**到 `<游戏安装目录>\mod\`，重命名为 `CatchTheCursorGame`。
4. 打开 `mod\CatchTheCursorGame\info.lps`，**删除 `itemid#3210888681:` 行**（跳过创意工坊校验的关键）。
5. 用本仓库的 DLL 覆盖 `plugin\CatchTheMousev1.10.dll`。
6. 启动游戏，MOD 列表中「抓光标」应显示启用，移动鼠标宠物会追逐光标。

> ⚠️ **不要直接修改创意工坊目录里的文件**——Steam 会校验并还原，导致 MOD 加载失败。详细步骤见 [变更说明](变更说明.md) 第 7 节。

### 兼容性

- 需要游戏运行时为 **.NET 10**（gamever ≥ 11057）。
- .NET 9 及更早的游戏版本请继续使用原社区修复版（Harmony 2.4.1）。

### 仓库文件

| 文件 | 说明 |
|---|---|
| `CatchTheMousev1.10.dll` | 修复版插件（Harmony 2.4.2 内嵌，单文件、无外部依赖） |
| [变更说明.md](变更说明.md) | 完整变更文档（根因分析、技术方案、安装与回滚） |

### 致谢

- 原插件作者：**cccccchara**（Steam 创意工坊 itemid 3210888681）
- 社区修复：[WuChangqing1/VPet-CatchTheCursor-Fix](https://github.com/WuChangqing1/VPet-CatchTheCursor-Fix)
- 本修复仅升级 Harmony 运行时版本，插件功能与原 MOD 保持一致

---

## English

### What this is

A fix for the VPet (虚拟桌宠模拟器) plugin **CatchTheCursor**, which makes your desktop pet chase and catch the mouse cursor. After VPet moved to the .NET 10 runtime (CoreCLR 10.0.12), the plugin — including the existing community fix — crashes on load:

```
HarmonyLib.HarmonyException
 ---> System.PlatformNotSupportedException: CoreCLR version 10.0.12 is not supported
```

The plugin DLL embeds Harmony 2.4.1, whose MonoMod.Core backend only supports up to .NET 9. This repository upgrades the embedded stack to **Harmony 2.4.2** and re-merges everything back into a single file. The plugin's own code is untouched.

### Installation

**Prerequisites: fully exit VPet and unsubscribe the workshop version of CatchTheCursor.**

#### Option A — existing local mod folder (simplest)

Download `CatchTheMousev1.10.dll` from [Releases](../../releases) and overwrite:

```
<Game install dir>\mod\CatchTheCursorGame\plugin\CatchTheMousev1.10.dll
```

#### Option B — convert your workshop copy to a local mod

1. Fully exit the game and unsubscribe the workshop mod in Steam.
2. Locate the workshop files: `steamapps\workshop\content\<VPet AppID>\3210888681\`.
3. **Copy** the whole folder to `<Game install dir>\mod\` and rename it to `CatchTheCursorGame`.
4. Edit `mod\CatchTheCursorGame\info.lps` and **delete the line `itemid#3210888681:`** — this makes the game treat it as a local mod and skip workshop verification.
5. Overwrite `plugin\CatchTheMousev1.10.dll` with the DLL from this repo.
6. Launch the game. The pet should now chase your cursor.

> ⚠️ **Do not edit files inside the Steam workshop folder directly** — Steam verifies and restores them, and the mod will fail to load. See [变更说明](变更说明.md) §7 for full steps.

### Compatibility

- Requires the game runtime to be **.NET 10** (gamever ≥ 11057).
- On .NET 9 or earlier game builds, keep using the original community fix (Harmony 2.4.1).

### Repository files

| File | Description |
|---|---|
| `CatchTheMousev1.10.dll` | Fixed plugin (Harmony 2.4.2 embedded, single file, no external dependencies) |
| [变更说明.md](变更说明.md) | Full changelog (root cause, technical approach, install & rollback) |

### Credits

- Original plugin by **cccccchara** (Steam Workshop itemid 3210888681)
- Community fix: [WuChangqing1/VPet-CatchTheCursor-Fix](https://github.com/WuChangqing1/VPet-CatchTheCursor-Fix)
- This fix only upgrades the embedded Harmony runtime; plugin behavior is unchanged
