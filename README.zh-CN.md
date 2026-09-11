# CustomFireSupport — 设置文件教程

<div align="center">

[![English](https://img.shields.io/badge/English-0969da?style=flat-square)](README.md)
[![简体中文](https://img.shields.io/badge/简体中文-2ea44f?style=flat-square)](README.zh-CN.md)

[English](README.md) · **简体中文** 

</div>

> 适用游戏：**Gunner, HEAT, PC!**（MelonLoader mod）
> 依赖：MelonLoader 0.6.x + Harmony（随 MelonLoader 附带）
> 版本：**v1.0.0**

本 mod 在任务地图的「火力支援」面板上提供 **6 个可自定义槽位**。
所有行为都由 `Bin\UserData\MelonPreferences.cfg` 里的 `[CustomFireSupport]` 段决定 —— **不用改代码、不用重编译**。

本文只写**玩家自己能设置的东西**：每一项的含义与可取值。

火力支援的思路源自 [GHPC Artillery Rework](https://github.com/QwertyRyo/GHPC_Artillery_Rework)（QwertyRyo）
—— 见 [许可证与致谢](#8-许可证与致谢)。

## 默认布局（6 个槽位默认全部开启）

| 槽位 | 默认用途 | 默认打法 / 弹种 |
|---|---|---|
| 槽 1 | 常规炮击 | HE 人员杀伤 |
| 槽 2 | 烟幕 | 烟幕弹 |
| 槽 3 | 照明弹 | 照明弹 |
| 槽 4 | CAS 空中支援 | 机炮扫射 |
| 槽 5 | CAS 空中支援 | 火箭弹 |
| 槽 6 | CAS 空中支援 | 炸弹 |

用不到的槽位把它的 `Enabled` 改成 `false` 即可；想把某个槽改成别的用途，见 §4 的取值清单。

---

## 0. 安装与生效

1. 到本仓库的 **Releases** 页面下载最新版压缩包 `CustomFireSupport-v1.0.0.zip`。
2. 把压缩包里的 `CustomFireSupport.dll` 复制到 `<游戏目录>\Bin\Mods\`。
3. 把压缩包里的 `cas_assets` 复制到同一个目录 `<游戏目录>\Bin\Mods\`（**必须与 DLL 同目录**）。
   它是随 mod 发布的资产包，装着 CAS 机型、挂载、硬点与烟幕/照明抛射物；装了才能保证每一关都能用上
   飞机与烟幕/照明。

   装好后 `Bin\Mods\` 是这样：

   ```
   Bin\Mods\CustomFireSupport.dll
   Bin\Mods\cas_assets
   ```

4. 启动一次游戏（进主菜单即可），mod 会在 `MelonPreferences.cfg` 里写好 `[CustomFireSupport]` 段。
   压缩包里还带一份 `CustomFireSupport.cfg`，内容与它完全一致，可以直接覆盖那一段。

   日志关键字：`[CustomFireSupport]`；日志文件：`Bin\MelonLoader\Latest.log`。

---

## 1. 书写约定

| 项目 | 说明 |
|---|---|
| 段名 | 只有一个段 `[CustomFireSupport]`，所有键都写在里面 |
| 键名前缀 | 槽位键一律带 `SlotN_` 前缀，`N` 为 1~6，如 `Slot3_Missions` |
| 布尔值 | 可写 `true` / `false`，也接受 `1` / `0` / `yes` / `no` / `on` / `off` |
| 字符串 | 引号可加可不加，如 `Type = Artillery` 与 `Type = "Artillery"` 等价 |
| 注释 | 支持整行注释，也支持行尾 `# 注释`（引号内的 `#` 不算注释） |
| 大小写 | 所有键名与取值都不区分大小写 |
| 倍率语义 | `1.0` = 游戏原值，`0.5` = 一半，`0` / `-1` = 关闭（瞬间 / 无冷却 / 零散布） |
| 重复键 | 同名键以最后一个为准 |
| 缺省 | 缺少某个键 = 使用该键的默认值，不需要把 98 个键写全 |

**键总数**：全局 8 个 + 槽位 15 个 × 6 槽 = **98 个**。

---

## 2. 全局设置（`[CustomFireSupport]`，8 键）

| 键 | 类型 | 默认 | 可取值 | 含义 |
|---|---|---|---|---|
| `Enabled` | 布尔 | `true` | `true` / `false` | 总开关。`false` = mod 完全不动作。 |
| `HideVanillaFireSupport` | 布尔 | `true` | `true` / `false` | `true` = 地图面板只显示你的槽位。官方炮组/架次数据本身不被修改，任务脚本安排的炮击/空袭照常执行，只是按钮不显示。 |
| `IlluminationOnlyAtNight` | 布尔 | `true` | `true` / `false` | `true` = 照明槽位只在夜间出现（白天自动隐藏）；`false` = 白天也能用。 |
| `SmokeOnlyDuringDay` | 布尔 | `true` | `true` / `false` | `true` = 烟幕槽位只在白天出现（夜间自动隐藏）；`false` = 全天可用。 |
| `VerboseLogging` | 布尔 | `false` | `true` / `false` | `true` = 把模板发现、槽位解析等排障细节打印到日志。 |
| `CasDeployDistanceMeters` | 小数 | `8000.0` | `100` ~ `60000` | 仅用于**关卡没有自带 CAS 管理器**的任务：飞机起飞/脱离点离玩家起始位置多远（米）。 |
| `CasDeployBearingDegrees` | 小数 | `180.0` | `0` ~ `360`（超出按 360 回绕） | 上述起飞点的方位角（度）。`0` = +Z（北），顺时针增大。 |
| `CasPrewarmKeys` | 字符串 | `"auto"` | `auto` / 逗号或分号分隔的键名 / 空 | 会话启动时额外预加载的资源键。`auto` = 不额外加载（CAS 机型/挂载/硬点与烟幕照明都由 `cas_assets` 资产包提供，无需额外键）；留空 = 关闭该功能。 |

---

## 3. 槽位设置（`SlotN_*`，每槽 15 键）

> 下表的 `SlotN_` 实际使用时把 `N` 换成 1~6。
> 「默认」一列按默认布局给出。

| 键 | 类型 | 默认 | 可取值 | 含义 |
|---|---|---|---|---|
| `SlotN_Enabled` | 布尔 | `true` | `true` / `false` | 该槽位开关。默认六个槽全部开启。 |
| `SlotN_Type` | 字符串 | 槽 1 = `Artillery`；槽 2 = `ArtillerySmoke`；槽 3 = `ArtilleryIllumination`；槽 4/5/6 = `CASSupport` | 见 §4.1 | 槽位类型。**写错会让整个槽位失效并在日志里警告**。 |
| `SlotN_DisplayName` | 字符串 | 按默认布局给（`Custom Artillery` / `Custom Smoke` / `Custom Illumination` / `CAS Gun Run` / `CAS Rockets` / `CAS Bombs`） | 任意文本；留空 = 用默认名 | 地图按钮上显示的名字。 |
| `SlotN_Missions` | 整数 | 槽 1/2/3 = `3`；槽 4/5/6 = `2` | `-1`，或 `0` ~ `999` | 可用**呼叫次数**（CAS 是**架次**）。`-1` = 无限（按钮显示 99，且每次呼叫后次数会补回）。`0` = 无法点击。 |
| `SlotN_RoundsPerCall` | 整数 | `-1` | `-1`（或任何小于 1 的值），或 `1` ~ `999` | **单次呼叫的弹数**（仅火炮）。`-1` = 沿用游戏炮组自己的弹数；`>0` = 强制指定。 |
| `SlotN_Munition` | 字符串 | 槽 2 = `Smoke`；槽 3 = `Illumination`；其余 = `AntiPersonnel` | 见 §4.2；留空 = 跟随 `Type` | **弹种**（仅火炮）。与 `Type` 不匹配时仍会生效，但会记一条警告（按钮图标/标记可能看着别扭）。 |
| `SlotN_Weapon` | 字符串 | `Any` | 见 §4.3 | 影响面板上显示的武器类别（`WeaponType`）。 |
| `SlotN_ImpactDelaySeconds` | 小数 | `1.0` | `-1` ~ `100` | **首弹延迟倍率**（只管"呼叫到第一发落地"）。`1` = 游戏原值；`0`~`1` 之间 = 取消延迟、首弹立即到；`0` / 负数 = **整梭子在同一帧打完**。**不影响发间隔**。 |
| `SlotN_InterShotDelaySeconds` | 小数 | `1.0` | `-1` ~ `100` | **发间隔倍率**（只管两发之间隔多久）。`1` = 原值（多数炮组约 0.7 秒）；`0.5` = 一半；`0` = 所有弹在同一帧打完。**不影响首弹延迟**。 |
| `SlotN_DispersionMeters` | 小数 | `1.0` | `-1` ~ `100` | **散布倍率**。`1` = 原半径；`0.5` = 一半；`0` / `-1` = 所有弹落在同一点。 |
| `SlotN_CooldownSeconds` | 小数 | `1.0` | `-1` ~ `100` | **冷却**。火炮 = 倍率（`1` = 原值）；CAS = 直接秒数（不沿用游戏原生的 120 秒）。`-1` / `0` = 无冷却；`0.5` = 一半。 |
| `SlotN_AmmoName` | 字符串 | `""` | 任意文本；留空 = 不筛选 | 可选筛选用：**弹药名包含该文本**（不区分大小写）才会被选中，例如 `"155mm"`。留空 = 取该弹种排序最优的模板。 |
| `SlotN_CasFlyover` | 字符串 | `""` | `""` / `SinglePass` / `Linger` | CAS **飞行方式**。留空 = 按机型自动（A-10 / SU-25 盘旋，其余机型单次通过）；`SinglePass` = 单次通过；`Linger` = 盘旋约 60 秒。仅 CAS 槽使用。 |
| `SlotN_CasAccuracy` | 小数 | `1.0` | `-1` ~ `100` | CAS **落点圆半径 = 值 × 15 米**：`0`（或 `-1`）= 半径 0 = 每一发都飞进锁定目标中心（**必中**）；`0.2` = 3 米；`0.5` = 7.5 米；`1` = 15 米；`>1` = 更大。**对空地导弹无效**（它每架次 1 发、永远直飞目标中心）。仅 CAS 槽使用。 |
| `SlotN_CasAttackTypes` | 字符串 | 槽 4 = `GunRun`；槽 5 = `Rockets`；槽 6 = `Bombs`；其余 = `Any` | `Any`，或逗号分隔列表，见 §4.4 | CAS **攻击方式筛选**。只会保留机型实际挂载的类型。仅 CAS 槽使用。 |

**仅火炮生效的键**：`RoundsPerCall`、`Munition`、`Weapon`、`ImpactDelaySeconds`、`InterShotDelaySeconds`、`DispersionMeters`、`AmmoName`。
**仅 CAS 生效的键**：`CasFlyover`、`CasAccuracy`、`CasAttackTypes`。
**两类都生效的键**：`Enabled`、`Type`、`DisplayName`、`Missions`、`CooldownSeconds`。

> `ImpactDelaySeconds` 与 `InterShotDelaySeconds` 是**两个互相独立**的键：一个只管首弹什么时候到，
> 一个只管弹与弹之间隔多久。想"首弹照原样、但把发间隔压一半"，就写 `ImpactDelaySeconds = 1.0` +
> `InterShotDelaySeconds = 0.5`，两者不会互相影响。
>
> 机型、弹药、生成高度、入射角、来向、再装填时间**不需要配置**：默认沿用关卡自己的数值。

---

## 4. 取值清单

### 4.1 `Type`（槽位类型）

| 取值 | 面板按钮 | 用途 |
|---|---|---|
| `Artillery` | Artillery | 常规炮击（HE / 反装甲） |
| `ArtillerySmoke` | Smoke | 烟幕遮蔽 |
| `ArtilleryIllumination` | Illum | 照明弹 |
| `CASSupport` | CAS | 固定翼空中支援 |

别名（不区分大小写）：`HE`、`Arty`、`Gun(s)`、`FireMission` → `Artillery`；`Smoke`、`SMK`、`WP`、`WhitePhosphorus` → `ArtillerySmoke`；`Illum`、`Flare` 等 → `ArtilleryIllumination`；`CAS`、`FixedWing` → `CASSupport`。

**写错的处理**：整个槽位被禁用，日志记一条警告并列出合法取值。


### 4.2 `Munition`（弹种，仅火炮）

| 取值 | 玩家看到的效果 |
|---|---|
| `AntiPersonnel` | HE 人员杀伤弹（默认） |
| `AntiArmor` | **集束子母弹**：撒下破甲杀伤双用途子弹药，命中载具能破甲；对周围步兵同样有效 |
| `Smoke` | 烟幕弹 |
| `Illumination` | 照明弹 |

别名：`AP`、`HE`、`Frag`、`HighExplosive` → `AntiPersonnel`；`AT`、`AntiTank`、`HEAT`、`APFSDS`、`Sabot` → `AntiArmor`。
留空 = 跟随 `Type`（烟幕槽 → `Smoke`，照明槽 → `Illumination`，其余 → `AntiPersonnel`）。
写错 = 记警告并使用上面的默认值，槽位仍然可用。

### 4.3 `Weapon`（武器类别，仅火炮）

`Guns` / `Mortars` / `Rockets` / `Any`（默认 `Any`）。
别名：`Gun`、`Howitzer`、`Cannon` → `Guns`；`Mortar` → `Mortars`；`Rocket`、`MLRS` → `Rockets`；留空 → `Any`。
写错 = 记警告并使用 `Any`。该项只影响面板上显示的武器类别。

### 4.4 `CasAttackTypes`（CAS 攻击方式）

取值（逗号分隔，可多选；`Any` 或留空 = 不筛选，用机型自带的全部挂载）：

| 取值 | 别名 | 效果 |
|---|---|---|
| `Bombs` | `Bomb` | 航弹 |
| `Rockets` | `Rocket` | 火箭弹 |
| `GunRun` | `Gun` / `Strafe` / `Cannon` | 航炮扫射 |
| `AirToGroundMissile` | `AGM` / `ATGM` / `Missile` | 空地导弹：只有 A-10 / MiG-23BN 能挂，每架次固定 1 发、永远 100% 命中锁定目标中心（`CasAccuracy` 对它无效） |

**只会保留机型实际挂载的类型**；列表里不认识的词会被忽略并记一条警告。


### 4.5 `CasFlyover`（CAS 飞行方式）

`SinglePass`（单次通过）/ `Linger`（盘旋约 60 秒）/ 留空或 `auto`（按机型自动，其余单次通过）。
别名：`Single`、`Pass`、`One` → `SinglePass`；`Loiter`、`Orbit`、`Stay` → `Linger`。

### 4.6 `CasPrewarmKeys`（全局）

`auto`（默认，不额外加载）/ 逗号或分号分隔的资源键名 / 空（关闭）。

---

## 5. 常用改法

| 想要的效果 | 怎么改 |
|---|---|
| 关掉某个槽位 | 把它的 `Enabled` 改成 `false`（例如不需要烟幕：`Slot2_Enabled = false`） |
| 槽 1 改打反装甲（集束子母弹） | `Slot1_Munition = "AntiArmor"` |
| 再加一个照明槽 | 找一个不用的槽，把它的 `Type` 设为 `ArtilleryIllumination`、`Munition` 设为 `Illumination` |
| CAS 槽换打法 | `SlotN_CasAttackTypes = "GunRun"` / `"Rockets"` / `"Bombs"` / `"AirToGroundMissile"` |
| CAS 一次投两种弹 | `SlotN_CasAttackTypes = "Bombs,Rockets"`（逗号组合） |
| 无限呼叫 | `SlotN_Missions = -1` |
| 取消冷却 | `SlotN_CooldownSeconds = -1` |
| 首弹立即到、之后照原速打 | `SlotN_ImpactDelaySeconds = 0.5`（`InterShotDelaySeconds` 保持 `1.0`） |
| 发间隔减半、首弹照原样延迟 | `SlotN_InterShotDelaySeconds = 0.5`（`ImpactDelaySeconds` 保持 `1.0`） |
| 瞬时齐射（同一帧打完） | `SlotN_ImpactDelaySeconds = 0` + `SlotN_InterShotDelaySeconds = 0` |
| 所有弹落在同一点 | `SlotN_DispersionMeters = 0` |
| CAS 必中 | `SlotN_CasAccuracy = 0` |
| 不要飞机只要炮 | 槽 4/5/6 的 `Enabled` 全部改成 `false` |
| 干净的地图面板 | `HideVanillaFireSupport = true`（默认） |

---

## 6. 容错与边界

- **所有数值都会被夹到合法区间**：写超范围的值不会崩溃，也不会产生荒谬结果，只会按边界值使用。
- **写错的枚举值**：`Type` 会让槽位禁用（有警告）；`Munition` / `Weapon` / `CasFlyover` / `CasAttackTypes` 会回退到默认值（有警告）。
- **写错的布尔值**：回退到该键的默认值。
- **槽位构建失败**：例如该关卡既找不到可用的炮弹模板、也找不到任何飞机，则该槽位不出现并在日志里写明原因；
  若所有槽位都构建失败，mod 会放弃隐藏官方按钮，游戏表现与未装 mod 一致。


---

## 7. 完整键位速查（98 键）

| 分组 | 键 |
|---|---|
| 全局（8） | `Enabled`、`HideVanillaFireSupport`、`IlluminationOnlyAtNight`、`SmokeOnlyDuringDay`、`VerboseLogging`、`CasDeployDistanceMeters`、`CasDeployBearingDegrees`、`CasPrewarmKeys` |
| 每槽（15 × 6） | `SlotN_Enabled`、`SlotN_Type`、`SlotN_DisplayName`、`SlotN_Missions`、`SlotN_RoundsPerCall`、`SlotN_Munition`、`SlotN_Weapon`、`SlotN_ImpactDelaySeconds`、`SlotN_InterShotDelaySeconds`、`SlotN_DispersionMeters`、`SlotN_CooldownSeconds`、`SlotN_AmmoName`、`SlotN_CasFlyover`、`SlotN_CasAccuracy`、`SlotN_CasAttackTypes` |

压缩包里的 `CustomFireSupport.cfg`（仓库里是 `docs\SampleConfig.cfg`）就是这 98 个键的完整预设，含逐键中文注释，可以直接照抄改。

---

## 8. 许可证与致谢

采用 **GNU Affero General Public License v3.0**（见 [LICENSE](LICENSE)）。

**[GHPC Artillery Rework](https://github.com/QwertyRyo/GHPC_Artillery_Rework)**（作者 **QwertyRyo**）——
第一个把 GHPC 的火力支援做成"玩家可以自己改造的东西"的社区 mod。本 mod 的方向源自它，在此致谢。
**与它没有代码共用**：这里的火力支援系统（槽位、CAS 载荷工厂、集束弹）都是本 mod 的独立实现。
