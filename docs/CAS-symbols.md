# CustomFireSupport — CAS symbol reference

> This document is the **authoritative symbol list** for the game's CAS subsystem as referenced by the
> `CustomFireSupport` module. Every entry comes from the game's .NET decompiled source
> (Assembly-CSharp) and records, item by item: the fully-qualified symbol name, what it is for, how it is
> accessed, and its source file and line number.
>
> **Important**: there are no "memory addresses" in a Unity project's decompiled C# source. GHPC is a
> .NET/Mono program, so symbol addresses are randomised on every launch and change on every update.
> MelonLoader + Harmony locate their targets at runtime by **symbol name** (type full name + member
> signature). This document therefore expresses an "address" as a fully-qualified name + signature, which
> is also the only kind of address the patch code can actually rely on and which stays stable across
> versions.

---

## 0. Namespaces and assemblies

- Game main assembly: `Assembly-CSharp` (the mod compiles against
  `publicized_assemblies\Assembly-CSharp-Publicized.dll`).
- Core CAS namespaces:
  - `GHPC.Vehicle` — loadouts / hardpoints / flight controller
  - `GHPC.Weaponry.CAS` — sorties and the support manager
  - `GHPC.Weaponry` — ordnance code (`AmmoCodexScriptable`)
  - `GHPC.Weaponry.Interfaces` — `IMapSupportInfo`
  - `GHPC.UI.Map` / `GHPC.UI` — map panel / `MapController`
  - `GHPC.Event` / `GHPC.Mission` — scripted CAS events
  - global namespace — `CASLoadoutScriptable`

---

## 1. Enums and data classes

### 1.1 `GHPC.Vehicle.CASAttackType` (enum)
- Source file: `GHPC/Vehicle/CASAttackType.cs`
- Purpose: the unique identifier of an attack type.
- Values:
  | Name | Value |
  |---|---|
  | `Bombs` | 0 |
  | `Rockets` | 1 |
  | `AirToGroundMissile` | 2 |
  | `AirToAirMissile` | 3 |
  | `GunRun` | 4 |
  | `Inert` | 5 |
- Note: `Inert` is the sentinel for "no usable attack type"; it is not a real weapon.

### 1.2 `GHPC.Vehicle.CASAttackMeta` (class, `[Serializable]`)
- Source file: `GHPC/Vehicle/CASAttackMeta.cs`
- Purpose: the flight / firing tuning of one attack type.
- Public fields (the mod may assign them directly when constructing):
  | Field | Type | Default | Purpose |
  |---|---|---|---|
  | `UniqueType` | `CASAttackType` | — | the attack type it belongs to |
  | `TriggerPulls` | `int` | 1 | number of trigger pulls (>1 runs the MultiFire coroutine) |
  | `TriggerPullInterval` | `float` | 0.1 | interval between trigger pulls (seconds) |
  | `FireAllAtOnce` | `bool` | false | fire every hardpoint simultaneously |
  | `ApproachDistance` | `float` | 1500 | distance at which fine aiming starts (metres) |
  | `ReleaseDistance` | `float` | 1000 | distance at which release / firing starts (metres) |
  | `PreDelay` | `float` | 0.5 | delay after reaching the release point, before firing (seconds) |
  | `PostDelay` | `float` | 0.5 | delay after firing, before breaking off (seconds) |
- Private fields (the mod does not write them): `_hardpointsIncluded`, `_firingIndex`
- Methods: `DeepCopy()`, `Init(List<CASHardpoint>)`, `Fire(Vector3, Transform)`, `GetNextAmmoType()`, `GetInheritVelocity()`

### 1.3 `GHPC.Vehicle.CASLoadout` (class, `[Serializable]`)
- Source file: `GHPC/Vehicle/CASLoadout.cs`
- Purpose: a payload; it can be constructed from just its two public fields:
  - `GameObject[] HardpointPrefabs` — hardpoint prefabs (length 1 = reuse for every hardpoint)
  - `CASAttackMeta[] Attacks` — the attack entries

### 1.4 `CASLoadoutScriptable` (ScriptableObject, global namespace)
- Source file: `CASLoadoutScriptable.cs`
- Purpose: the SO shell around `CASLoadout`; the type of `CasAirframeUnit.Loadout`.
- Field: `public CASLoadout Loadout;`

### 1.5 `GHPC.Faction` (enum)
- Source file: `GHPC/Faction.cs`
- Values: `Neutral=0, Red=1, Green=2, Blue=3`

---

## 2. Hardpoints and ordnance

### 2.1 `GHPC.Vehicle.CASHardpoint` (MonoBehaviour)
- Source file: `GHPC/Vehicle/CASHardpoint.cs`
- Purpose: a single weapon station — the smallest unit that decides "what can be fired".
- Public read-only properties (read by `CASHardpointManager` / the mod):
  | Member | Type | Source line |
  |---|---|---|
  | `Type` | `CASAttackType` | line 62 (returns the private `_type`) |
  | `Ammo` | `AmmoType` | line 64 (from `_ammo.AmmoType`) |
  | `InheritVelocity` | `bool` | line 66 |
  | `IsEmpty` | `bool` | line 86 |
- **Private serialised fields (no public setter; the mod writes them with `AccessTools.FieldRefAccess`)**:
  | Field | Type | Line | Purpose |
  |---|---|---|---|
  | `_type` | `CASAttackType` | 15 | the hardpoint's attack type |
  | `_ammo` | `AmmoCodexScriptable` | 19 | the ordnance it fires |
  | `_audioEvent` | `string` | 24 | firing sound (optional) |
  | `_launchDeviation` | `float` | 28 | launch dispersion angle (degrees, full arc) |
  | `_inheritVelocity` | `bool` | 32 | the projectile inherits the carrier's velocity |
  | `_visibleMunitions` | `bool` | 36 | visible-ordnance mode (the mounted-round models) |
  | `_visibleMunitionObjects` | `GameObject[]` | 41 | visible round models (used when `_visibleMunitions=true`) |
  | `_munitionSpawnPoints` | `GameObject[]` | 46 | spawn points of the visible rounds |
  | `_munitionCount` | `int` | 51 | rounds available for the invisible rounds |
  | `_munitionSpawnPoint` | `Transform` | 56 | spawn point of the invisible rounds |
  | `_launchCount` | `int` | 60 | rounds fired per trigger pull (default 1) |
- Methods: `Fire(Vector3, Transform)`, `DoBallisticsCache(...)`

### 2.2 `GHPC.Vehicle.CASHardpointManager` (MonoBehaviour)
- Source file: `GHPC/Vehicle/CASHardpointManager.cs`
- Purpose: mounts a `Loadout` onto `HardpointAttachPoints` and executes attacks.
- Public members:
  | Member | Type | Line | Purpose |
  |---|---|---|---|
  | `HardpointAttachPoints` | `Transform[]` | 11 | the attach points (decides how many can be carried) |
  | `CurrentAttackType` | `CASAttackType` | 20 | the current attack type |
  | `DoConfig()` | method | 78 | mount the hardpoints; returns immediately when `HasCriticalConfigError()` is true |
  | `CanDoAttackType(type)` | method | 99 | whether that attack type is available (looks at `_hasXxx`) |
  | `Fire(type)` | method | 121 | execute the attack (refused while `_configDone=false`) |
  | `GetAttackMetaByType(type)` | method | 215 | fetch the attack metadata |
  | `SetLoadout(CASLoadoutScriptable)` | method | 73 | set the loadout |
- **Private fields**:
  | Field | Type | Line | Purpose |
  |---|---|---|---|
  | `Loadout` | `CASLoadoutScriptable` | 18 | the current loadout (read by `SetUpHardpoints`) |
  | `_configDone` | `bool` | 28 | configured flag (false → refuses to fire) |
  | `_hasBombs` … `_hasInert` | `bool`×6 | 32-42 | availability of each attack type |
  | `_attacks` | `List<CASAttackMeta>` | 30 | the attack entries (copied from `Loadout.Attacks`) |
- **Critical rule**: `HasCriticalConfigError()` (line 246) requires `HardpointPrefabs` to be non-empty and
  **either length 1 or ≥ the number of hardpoints**, otherwise `DoConfig` returns early → never
  configured → never fires.

### 2.3 `GHPC.Vehicle.HardpointWeaponAvailabilityState` (class)
- Source file: `GHPC/Vehicle/HardpointWeaponAvailabilityState.cs`
- Purpose: the per-hardpoint capability flags plus the `BecameEmpty` event (`Action<CASHardpoint>`).

### 2.4 `GHPC.Weaponry.AmmoCodexScriptable` (ScriptableObject)
- Source file: `GHPC/Weaponry/AmmoCodexScriptable.cs`
- Purpose: the ordnance code; its only field is `public AmmoType AmmoType;` (line 9). `AmmoType` lives in
  the `GHPC.Weapons` namespace.

---

## 3. Flight controller

### 3.1 `GHPC.Vehicle.CASController` (MonoBehaviour, `IAircraft`, `IUnitAI`)
- Source file: `GHPC/Vehicle/CASController.cs`
- Purpose: the flight / aiming / attacking AI of a CAS aircraft.
- Public fields (written after `SendCasSupport` instantiates it):
  | Field | Type | Line | Purpose |
  |---|---|---|---|
  | `casManager` | `CasSupportManager` | 120 | the owning manager |
  | `unitFaction` | `Faction` | 117 | faction |
  | `orbitPoint` | `Vector3` | 126 | orbit / observation point |
  | `exfilGoal` | `Vector3` | 128 | exfil point |
  | `movePointGoal` | `Vector3` | 124 | movement target |
  | `cruiseAltitude` | `float` | 112 | cruise altitude |
  | `targetAltitude` | `float` | 55 | target altitude |
  | `ObservationPointOffset` | `float` | 115 | observation point offset (default 1000) |
  | `RemainingFlightTime` | `float` | 130 | remaining flight time (`LingerTime`=60) |
  | `passes` | `float` | 132 | -1 = linger, 1 = single pass |
  | `frameNum` | `int` | 134 | index into the sortie array |
  | `FinalTarget` | `Unit` | 122 | the current target |
- Public methods:
  | Method | Line | Purpose |
  |---|---|---|
  | `SetLoadout(CASLoadoutScriptable)` | 746 | set the loadout and `DoConfig()` |
  | `SetInterestPoint(Vector3)` | 941 | set the interest point |
  | `UpdateAI()` | 386 | the main AI update |
- Private (affected by Harmony patches, not called directly):
  | Member | Line | Purpose |
  |---|---|---|
  | `GetIdealAttackType(Unit)` | 1117 | **picks the attack type by target-type priority** (GunRun usually comes last) |
  | `_hardpointManager` | 219 | located while configuring |
  | `_finalAttackType` | 205 | the chosen attack type |
  | `_releaseDistance` / `_approachDistance` | 197 / 199 | distance thresholds |

---

## 4. Sortie and support managers

### 4.1 `GHPC.Weaponry.CAS.CasAirframeUnit` (class, `IMapSupportInfo`, `[Serializable]`)
- Source file: `GHPC/Weaponry/CAS/CasAirframeUnit.cs`
- Purpose: one callable CAS sortie.
- Public fields:
  | Field | Type | Line | Purpose |
  |---|---|---|---|
  | `airframePrefab` | `GameObject` | 20 | the aircraft prefab |
  | `Loadout` | `CASLoadoutScriptable` | 22 | the loadout |
  | `flyoverType` | `FlyoverType` | 24 | `SinglePass=0` / `Linger=1` |
- Public properties / methods:
  | Member | Line | Purpose |
  |---|---|---|
  | `FriendlyName` | 29 | returns `airframePrefab.name` |
  | `RemainingMissions` | 31 | remaining sorties (private `_missionsAvailable`) |
  | `IsReady` | 33 | `_missionsAvailable>0 && RemainingCooldown<=0` |
  | `RemainingCooldown` | 45 | remaining cooldown (private set) |
  | `Reset()` | 52 | clears the cooldown |
  | `ResetCooldown()` | 57 | cooldown = `RechargeTime`(120) |
  | `ReduceMissionsAvailable()` | 47 | sorties -1 |
  | `DoUpdate(float)` | 62 | decrements the cooldown |
- Static constants: `RechargeTime = 120f` (line 16), `LingerTime = 60f` (line 18).
- Private field: `_missionsAvailable` (line 27, default 1).

### 4.2 `GHPC.Weaponry.CAS.CasSupportManager` (MonoBehaviour)
- Source file: `GHPC/Weaponry/CAS/CasSupportManager.cs`
- Purpose: the two factions' sortie arrays plus call dispatch.
- Public fields: `BlueCasAirframes` (16), `RedCasAirframes` (22), `delayBetweenUses` (10).
- Public methods:
  | Method | Line | Purpose |
  |---|---|---|
  | `SendCasSupport(Vector3, Faction, int casIndex=-1, bool yayFreePlane=false, Unit cheatTargetUnit=null)` | 36 | the call; when `casIndex<0` it takes the **first `IsReady`** |
  | `SetAirframes()` | 26 | caches the player faction's array |
  | `HasAirframesAvailable()` | 31 | whether any sortie is available |
  | `ReturnAirframe(int, Faction)` | 106 | return a sortie (resets the cooldown) |
  | `CheckNextAvailableAirframe()` | 111 | cooldown of the next available sortie |
- Private: `PlayerFactionAirframes` (24), `blueDeployPoint` / `redDeployPoint` (14 / 20, `[SerializeField]`).

### 4.3 `GHPC.Weaponry.Interfaces.IMapSupportInfo` (interface)
- Source file: `GHPC/Weaponry/Interfaces/IMapSupportInfo.cs`
- Members: `FriendlyName`, `RemainingMissions`, `RemainingCooldown` (read-only properties).

---

## 5. UI / calls / events

### 5.1 `GHPC.UI.Map.MapControlFlag` (`[Flags]` enum)
- Source file: `GHPC/UI/Map/MapControlFlag.cs`
- Values: `None=0, Artillery=1, CASSupport=2, CASHeliSupport=4, ArtilleryIllumination=8, ArtillerySmoke=0x10`.

### 5.2 `GHPC.UI.Map.MapFireSupportPanel.AddButton(MapControlFlag, IMapSupportInfo)`
- Source file: `GHPC/UI/Map/MapFireSupportPanel.cs:223`
- Key behaviour: once any `CASSupport` button exists, every later `AddMapSupportInfo()` is **merged** into
  it and never creates a new one (lines 230-235); for CAS, `SupportName` is hardcoded to `"Fixed Wing"`
  (line 240).

### 5.3 `GHPC.UI.MapController`
- Source file: `GHPC/UI/MapController.cs`
- `InitControlState` (coroutine, around line 800): `_casSupportManager.SetAirframes()` (862) →
  `AppendFlag(CASSupport, …)` (866) → `TryCallCAS` subscribes to map clicks (865).
- `TryCallCAS` (1311): `SendCasSupport(args.MapHitPosition, PlayerInput.Instance.CurrentPlayerUnit.Allegiance)`
  (1322) — **it does not pass a casIndex**.

### 5.4 `GHPC.Event.CloseAirSupportAction`
- Source file: `GHPC/Event/CloseAirSupportAction.cs`
- Fields: `_team`, `_useDynamicMode`, `_dynamicTeam`, `_impactMarker`, `_casTypeIndex` (default -1).
- `ActionExecute()`: `SendCasSupport(_impactMarker.position, unitFaction, _casTypeIndex)` (line 51).

### 5.5 `GHPC.Mission.NoGoZone`
- Source file: `GHPC/Mission/NoGoZone.cs`
- Field: `CasListIndex` (line 24).
- The penalty call: `SendCasSupport(unit.transform.position, unitFaction, CasListIndex, yayFreePlane: true, unit)`
  (line 244).

---

## 6. The three layers of "first come, first served" (why custom sorties must take the lowest indices)

| Layer | Symbol | Behaviour |
|---|---|---|
| Button | `MapFireSupportPanel.AddButton` | CAS entries merge into one button, in call order |
| Panel | `ActiveSupportInfo` | `SupportInfos.FirstOrDefault(RemainingMissions>0)` |
| Dispatch | `CasSupportManager.SendCasSupport` | when `casIndex<0`, scans for the first `IsReady` |

`CustomSupportRegistry.InjectAirframes` therefore places the custom sorties at **indices 0..n-1** (the low
slots take over) and keeps the original `existing[i]` in the high slots, so the serialised indices of the
scripted events (`CloseAirSupportAction._casTypeIndex` / `NoGoZone.CasListIndex`) stay valid.

---

## 7. Maintenance

- This document is anchored to the current `Scripts/Assembly-CSharp` snapshot. If a game update changes the
  decompiled source, the line numbers in the tables above have to be re-checked.
- The field name of every `AccessTools.FieldRefAccess<T, F>("name")` in the mod source, and the target of
  every `[HarmonyPatch(typeof(X), "M")]`, must be based on this table alone — never on memory.
