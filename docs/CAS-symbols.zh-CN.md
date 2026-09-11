# CustomFireSupport — CAS 符号地址清单（Symbol Reference）

> 本文档是 `CustomFireSupport` 模块引用游戏 CAS 子系统的**权威符号清单**。
> 全部条目来自游戏的 .NET 反编译源码（Assembly-CSharp），逐条标注：符号全名、用途、访问方式、源文件与行号。
>
> **重要说明**：Unity 项目文件（反编译 C# 源码）里不存在"内存地址"。GHPC 是 .NET/Mono 程序，每次启动符号地址随机化、每次更新变化。MelonLoader + Harmony 通过**符号名**（类型全名 + 成员签名）在运行时定位目标，因此本文档用"符号地址"（fully-qualified name + signature）表达，这也是补丁代码里真正可依赖、且跨版本稳定的"地址"。

---

## 0. 命名空间与程序集

- 游戏主程序集：`Assembly-CSharp`（mod 通过 `publicized_assemblies\Assembly-CSharp-Publicized.dll` 编译引用）。
- CAS 核心命名空间：
  - `GHPC.Vehicle` — 挂载/硬点/飞行控制器
  - `GHPC.Weaponry.CAS` — 架次与支援管理器
  - `GHPC.Weaponry` — 弹药代码（`AmmoCodexScriptable`）
  - `GHPC.Weaponry.Interfaces` — `IMapSupportInfo`
  - `GHPC.UI.Map` / `GHPC.UI` — 地图面板 / `MapController`
  - `GHPC.Event` / `GHPC.Mission` — 脚本化 CAS 事件
  - 全局命名空间 — `CASLoadoutScriptable`

---

## 1. 枚举与数据类

### 1.1 `GHPC.Vehicle.CASAttackType`（枚举）
- 源文件：`GHPC/Vehicle/CASAttackType.cs`
- 用途：攻击方式的唯一标识。
- 取值：
  | 名 | 值 |
  |---|---|
  | `Bombs` | 0 |
  | `Rockets` | 1 |
  | `AirToGroundMissile` | 2 |
  | `AirToAirMissile` | 3 |
  | `GunRun` | 4 |
  | `Inert` | 5 |
- 备注：`Inert` 是"无可用打法"的哨兵，不是真实武器。

### 1.2 `GHPC.Vehicle.CASAttackMeta`（类，`[Serializable]`）
- 源文件：`GHPC/Vehicle/CASAttackMeta.cs`
- 用途：一种攻击方式的飞行/开火调参。
- 公开字段（构造时可由 mod 直接赋值）：
  | 字段 | 类型 | 默认 | 用途 |
  |---|---|---|---|
  | `UniqueType` | `CASAttackType` | — | 归属的攻击方式 |
  | `TriggerPulls` | `int` | 1 | 扣扳机次数（>1 走 MultiFire 协程） |
  | `TriggerPullInterval` | `float` | 0.1 | 多次扣扳机间隔（秒） |
  | `FireAllAtOnce` | `bool` | false | 全挂点同时发射 |
  | `ApproachDistance` | `float` | 1500 | 开始精瞄的距离（米） |
  | `ReleaseDistance` | `float` | 1000 | 开始投弹/发射的距离（米） |
  | `PreDelay` | `float` | 0.5 | 到达释放点后开火前延时（秒） |
  | `PostDelay` | `float` | 0.5 | 开火后脱离前延时（秒） |
- 私有字段（mod 不直接写）：`_hardpointsIncluded`、`_firingIndex`
- 方法：`DeepCopy()`、`Init(List<CASHardpoint>)`、`Fire(Vector3, Transform)`、`GetNextAmmoType()`、`GetInheritVelocity()`

### 1.3 `GHPC.Vehicle.CASLoadout`（类，`[Serializable]`）
- 源文件：`GHPC/Vehicle/CASLoadout.cs`
- 用途：挂载载荷，两个公开字段即可构造：
  - `GameObject[] HardpointPrefabs` — 硬点预制体（长度 1 = 复用全部挂点）
  - `CASAttackMeta[] Attacks` — 攻击条目列表

### 1.4 `CASLoadoutScriptable`（ScriptableObject，全局命名空间）
- 源文件：`CASLoadoutScriptable.cs`
- 用途：`CASLoadout` 的 SO 壳；`CasAirframeUnit.Loadout` 类型。
- 字段：`public CASLoadout Loadout;`

### 1.5 `GHPC.Faction`（枚举）
- 源文件：`GHPC/Faction.cs`
- 取值：`Neutral=0, Red=1, Green=2, Blue=3`

---

## 2. 硬点与弹药

### 2.1 `GHPC.Vehicle.CASHardpoint`（MonoBehaviour）
- 源文件：`GHPC/Vehicle/CASHardpoint.cs`
- 用途：单个武器挂点，是决定"能打什么"的最小单元。
- 公开只读属性（供 `CASHardpointManager`/mod 读取）：
  | 成员 | 类型 | 来源行 |
  |---|---|---|
  | `Type` | `CASAttackType` | 第 62 行（返回私有 `_type`） |
  | `Ammo` | `AmmoType` | 第 64 行（来自 `_ammo.AmmoType`） |
  | `InheritVelocity` | `bool` | 第 66 行 |
  | `IsEmpty` | `bool` | 第 86 行 |
- **私有序列化字段（无公开 setter，mod 通过 `AccessTools.FieldRefAccess` 反射写入）**：
  | 字段 | 类型 | 行号 | 用途 |
  |---|---|---|---|
  | `_type` | `CASAttackType` | 15 | 硬点攻击方式 |
  | `_ammo` | `AmmoCodexScriptable` | 19 | 发射的弹药 |
  | `_audioEvent` | `string` | 24 | 发射音效（可选） |
  | `_launchDeviation` | `float` | 28 | 发射散布角（度，总弧长） |
  | `_inheritVelocity` | `bool` | 32 | 弹丸继承载机速度 |
  | `_visibleMunitions` | `bool` | 36 | 可见弹药模式（挂弹模型） |
  | `_visibleMunitionObjects` | `GameObject[]` | 41 | 可见弹模型（`_visibleMunitions=true` 时用） |
  | `_munitionSpawnPoints` | `GameObject[]` | 46 | 可见弹发射点 |
  | `_munitionCount` | `int` | 51 | 隐形弹备弹数 |
  | `_munitionSpawnPoint` | `Transform` | 56 | 隐形弹发射点 |
  | `_launchCount` | `int` | 60 | 每次扣扳机发射数（默认 1） |
- 方法：`Fire(Vector3, Transform)`、`DoBallisticsCache(...)`

### 2.2 `GHPC.Vehicle.CASHardpointManager`（MonoBehaviour）
- 源文件：`GHPC/Vehicle/CASHardpointManager.cs`
- 用途：把 `Loadout` 装配到 `HardpointAttachPoints` 并执行攻击。
- 公开成员：
  | 成员 | 类型 | 行号 | 用途 |
  |---|---|---|---|
  | `HardpointAttachPoints` | `Transform[]` | 11 | 挂点列表（决定可挂几个） |
  | `CurrentAttackType` | `CASAttackType` | 20 | 当前攻击方式 |
  | `DoConfig()` | 方法 | 78 | 装配挂点；`HasCriticalConfigError()` 为真则直接返回 |
  | `CanDoAttackType(type)` | 方法 | 99 | 是否具备该打法（查 `_hasXxx`） |
  | `Fire(type)` | 方法 | 121 | 执行攻击（`_configDone=false` 时拒绝） |
  | `GetAttackMetaByType(type)` | 方法 | 215 | 取攻击元数据 |
  | `SetLoadout(CASLoadoutScriptable)` | 方法 | 73 | 设挂载 |
- **私有字段**：
  | 字段 | 类型 | 行号 | 用途 |
  |---|---|---|---|
  | `Loadout` | `CASLoadoutScriptable` | 18 | 当前挂载（`SetUpHardpoints` 读它） |
  | `_configDone` | `bool` | 28 | 装配完成标志（false → 拒绝开火） |
  | `_hasBombs` … `_hasInert` | `bool`×6 | 32-42 | 各打法可用性 |
  | `_attacks` | `List<CASAttackMeta>` | 30 | 攻击条目（复制自 `Loadout.Attacks`） |
- **关键规则**：`HasCriticalConfigError()`（第 246 行）要求 `HardpointPrefabs` 非空且**长度 ==1 或 ≥ 挂点数**，否则 `DoConfig` 早退 → 永不配置 → 永不发射。

### 2.3 `GHPC.Vehicle.HardpointWeaponAvailabilityState`（类）
- 源文件：`GHPC/Vehicle/HardpointWeaponAvailabilityState.cs`
- 用途：每硬点的能力标记 + `BecameEmpty` 事件（`Action<CASHardpoint>`）。

### 2.4 `GHPC.Weaponry.AmmoCodexScriptable`（ScriptableObject）
- 源文件：`GHPC/Weaponry/AmmoCodexScriptable.cs`
- 用途：弹药代码，唯一字段 `public AmmoType AmmoType;`（第 9 行）。`AmmoType` 在 `GHPC.Weapons` 命名空间。

---

## 3. 飞行控制器

### 3.1 `GHPC.Vehicle.CASController`（MonoBehaviour, `IAircraft`, `IUnitAI`）
- 源文件：`GHPC/Vehicle/CASController.cs`
- 用途：CAS 飞机的飞行/瞄准/攻击 AI。
- 公开字段（`SendCasSupport` 实例化后写入）：
  | 字段 | 类型 | 行号 | 用途 |
  |---|---|---|---|
  | `casManager` | `CasSupportManager` | 120 | 归属管理器 |
  | `unitFaction` | `Faction` | 117 | 阵营 |
  | `orbitPoint` | `Vector3` | 126 | 盘旋/观测点 |
  | `exfilGoal` | `Vector3` | 128 | 脱离点 |
  | `movePointGoal` | `Vector3` | 124 | 移动目标点 |
  | `cruiseAltitude` | `float` | 112 | 巡航高度 |
  | `targetAltitude` | `float` | 55 | 目标高度 |
  | `ObservationPointOffset` | `float` | 115 | 观测点偏移（默认 1000） |
  | `RemainingFlightTime` | `float` | 130 | 剩余飞行时间（`LingerTime`=60） |
  | `passes` | `float` | 132 | -1 = 盘旋，1 = 单次通场 |
  | `frameNum` | `int` | 134 | 架次数组索引 |
  | `FinalTarget` | `Unit` | 122 | 当前目标 |
- 公开方法：
  | 方法 | 行号 | 用途 |
  |---|---|---|
  | `SetLoadout(CASLoadoutScriptable)` | 746 | 设挂载并 `DoConfig()` |
  | `SetInterestPoint(Vector3)` | 941 | 设兴趣点 |
  | `UpdateAI()` | 386 | AI 主更新 |
- 私有（mod 经 Harmony patch 影响，不直接调用）：
  | 成员 | 行号 | 用途 |
  |---|---|---|
  | `GetIdealAttackType(Unit)` | 1117 | **按目标类型优先级选打法**（GunRun 常垫底） |
  | `_hardpointManager` | 219 | 装配时定位 |
  | `_finalAttackType` | 205 | 选定的攻击方式 |
  | `_releaseDistance` / `_approachDistance` | 197 / 199 | 距离阈值 |

---

## 4. 架次与支援管理器

### 4.1 `GHPC.Weaponry.CAS.CasAirframeUnit`（类，`IMapSupportInfo`, `[Serializable]`）
- 源文件：`GHPC/Weaponry/CAS/CasAirframeUnit.cs`
- 用途：一架可呼叫的 CAS 架次。
- 公开字段：
  | 字段 | 类型 | 行号 | 用途 |
  |---|---|---|---|
  | `airframePrefab` | `GameObject` | 20 | 飞机预制体 |
  | `Loadout` | `CASLoadoutScriptable` | 22 | 挂载 |
  | `flyoverType` | `FlyoverType` | 24 | `SinglePass=0` / `Linger=1` |
- 公开属性/方法：
  | 成员 | 行号 | 用途 |
  |---|---|---|
  | `FriendlyName` | 29 | 返回 `airframePrefab.name` |
  | `RemainingMissions` | 31 | 剩余架次（私有 `_missionsAvailable`） |
  | `IsReady` | 33 | `_missionsAvailable>0 && RemainingCooldown<=0` |
  | `RemainingCooldown` | 45 | 剩余冷却（私有 set） |
  | `Reset()` | 52 | 冷却清零 |
  | `ResetCooldown()` | 57 | 冷却 = `RechargeTime`(120) |
  | `ReduceMissionsAvailable()` | 47 | 架次 -1 |
  | `DoUpdate(float)` | 62 | 冷却递减 |
- 静态常量：`RechargeTime = 120f`（第 16 行）、`LingerTime = 60f`（第 18 行）。
- 私有字段：`_missionsAvailable`（第 27 行，默认 1）。

### 4.2 `GHPC.Weaponry.CAS.CasSupportManager`（MonoBehaviour）
- 源文件：`GHPC/Weaponry/CAS/CasSupportManager.cs`
- 用途：两阵营架次数组 + 呼叫派发。
- 公开字段：`BlueCasAirframes`（16）、`RedCasAirframes`（22）、`delayBetweenUses`（10）。
- 公开方法：
  | 方法 | 行号 | 用途 |
  |---|---|---|
  | `SendCasSupport(Vector3, Faction, int casIndex=-1, bool yayFreePlane=false, Unit cheatTargetUnit=null)` | 36 | 呼叫；`casIndex<0` 时取**第一个 `IsReady`** |
  | `SetAirframes()` | 26 | 缓存玩家阵营数组 |
  | `HasAirframesAvailable()` | 31 | 是否有架次 |
  | `ReturnAirframe(int, Faction)` | 106 | 归还架次（重置冷却） |
  | `CheckNextAvailableAirframe()` | 111 | 下个可用架次冷却 |
- 私有：`PlayerFactionAirframes`（24）、`blueDeployPoint`/`redDeployPoint`（14/20，`[SerializeField]`）。

### 4.3 `GHPC.Weaponry.Interfaces.IMapSupportInfo`（接口）
- 源文件：`GHPC/Weaponry/Interfaces/IMapSupportInfo.cs`
- 成员：`FriendlyName`、`RemainingMissions`、`RemainingCooldown`（只读属性）。

---

## 5. UI / 调用 / 事件

### 5.1 `GHPC.UI.Map.MapControlFlag`（`[Flags]` 枚举）
- 源文件：`GHPC/UI/Map/MapControlFlag.cs`
- 取值：`None=0, Artillery=1, CASSupport=2, CASHeliSupport=4, ArtilleryIllumination=8, ArtillerySmoke=0x10`。

### 5.2 `GHPC.UI.Map.MapFireSupportPanel.AddButton(MapControlFlag, IMapSupportInfo)`
- 源文件：`GHPC/UI/Map/MapFireSupportPanel.cs:223`
- 关键行为：已存在任一 `CASSupport` 按钮时，后续全部 `AddMapSupportInfo()` **合并**、永不新建（第 230-235 行）；`SupportName` 对 CAS 硬编码 `"Fixed Wing"`（第 240 行）。

### 5.3 `GHPC.UI.MapController`
- 源文件：`GHPC/UI/MapController.cs`
- `InitControlState`（协程，约 800 行）：`_casSupportManager.SetAirframes()`（862）→ `AppendFlag(CASSupport, …)`（866）→ `TryCallCAS` 订阅地图点击（865）。
- `TryCallCAS`（1311）：`SendCasSupport(args.MapHitPosition, PlayerInput.Instance.CurrentPlayerUnit.Allegiance)`（1322）——**不传 casIndex**。

### 5.4 `GHPC.Event.CloseAirSupportAction`
- 源文件：`GHPC/Event/CloseAirSupportAction.cs`
- 字段：`_team`、`_useDynamicMode`、`_dynamicTeam`、`_impactMarker`、`_casTypeIndex`（默认 -1）。
- `ActionExecute()`：`SendCasSupport(_impactMarker.position, unitFaction, _casTypeIndex)`（第 51 行）。

### 5.5 `GHPC.Mission.NoGoZone`
- 源文件：`GHPC/Mission/NoGoZone.cs`
- 字段：`CasListIndex`（第 24 行）。
- 惩罚调用：`SendCasSupport(unit.transform.position, unitFaction, CasListIndex, yayFreePlane: true, unit)`（第 244 行）。

---

## 6. 三层"先到先得"（决定自定义架次为何必须占最低索引）

| 层 | 符号 | 行为 |
|---|---|---|
| 按钮 | `MapFireSupportPanel.AddButton` | CAS 条目合并进同一按钮，按调用顺序 |
| 面板 | `ActiveSupportInfo` | `SupportInfos.FirstOrDefault(RemainingMissions>0)` |
| 派发 | `CasSupportManager.SendCasSupport` | `casIndex<0` 时扫描取第一个 `IsReady` |

因此 `CustomSupportRegistry.InjectAirframes` 把自定义架次放在**索引 0..n-1**（低位接管），高位保持原 `existing[i]`，使脚本化事件（`CloseAirSupportAction._casTypeIndex` / `NoGoZone.CasListIndex`）的序列化索引仍有效。

---

## 7. 更新维护

- 本文档锚定 `Scripts/Assembly-CSharp` 当前快照。游戏更新后若反编译源码变化，需重新核对上表行号。
- mod 源码里每个 `AccessTools.FieldRefAccess<T, F>("name")` 的字段名、每个 `[HarmonyPatch(typeof(X), "M")]` 的目标，都应以本表为唯一依据，避免凭记忆写错符号。
