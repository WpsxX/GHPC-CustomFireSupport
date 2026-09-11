# GHPC CAS real-asset inventory (source: AssetRipper export)

> Data source: the AssetRipper export of the game's main build `sharedassets*.assets` (`path_id_map.json`,
> 402 files).
> This document is the authoritative answer to "which real weapons / loadouts / airframes does CAS
> actually ship?" It is what the default data of `CasAirframeCatalog` / `CasAttackLibrary` /
> `CasPayloadFactory` is calibrated against.

## Key conclusions

- CAS content is concentrated in **`sharedassets2.assets`** (File 89 of path_id_map, 816 assets).
- **Fixed-wing airframe meshes**: A10, F104, F4, F4_USAF, F4_LW, MiG-21, MiG17, MiG23, MiG23_ussr, SU22,
  sov_airlaunched (the shared Soviet air-launched ordnance parts).
- **Each attack type against the real assets it maps to**:

  | Attack type | Real ordnance mesh | Real hardpoints / racks | Verdict |
  |---|---|---|---|
  | Bombs | mk82, FAB250, FAB501 (= FAB-500) | BRU-42, BRU045, BRU046, MBD3-U2T008 | ✅ real assets exist |
  | Rockets | s5_rocket, s8_rocket | ub16_pod, ub32_pod, b8_pod, MARS, MARS2_pod | ✅ real assets exist |
  | AirToGroundMissile | **AGM65, AGM066** (AGM-65 Maverick) | LAU089/090/114/117/118/119 series | ✅ real assets exist |
  | AirToAirMissile | **AIM013, AIM014** (AIM-9 Sidewinder) | LAU series | ✅ real assets exist |
  | GunRun | **no gun pod or hardpoint at all** | none | ❌ has to be built at runtime |

## Full ordnance / rack mesh inventory (sharedassets2.assets)

### Hardpoints and racks
- Rocket pods: `ub16_pod`, `ub32_pod`, `ub32_cone001`, `b8_pod` (in sharedassets86.assets), `MARS`, `MARS2_pod`
- Bomb racks: `BRU-42`, `BRU045`, `BRU046`, `MBD3-U2T008`
- LAU rails (shared by rockets and missiles): `LAU089`, `LAU090`, `LAU114`, `LAU117`, `LAU118`, `LAU119`, `LAU32`, `LAU_32`

### Ordnance
- Bombs: `mk82`, `FAB250`, `FAB501`
- Rockets: `s5_rocket`, `s8_rocket`
- Air-to-ground missiles: `AGM65`, `AGM066`
- Air-to-air missiles: `AIM013`, `AIM014`

### Airframe meshes
`A10`, `F104`, `F4`, `F4_USAF`, `F4_LW`, `MiG-21`, `MiG17`, `MiG23`, `MiG23_ussr`, `SU22`, `sov_airlaunched`

## Correspondence with the code

- The ammo keywords in `CasPayloadFactory.SideDefaults` are calibrated against those real mesh names:
  - NATO ATGM gained `agm65` / `agm66` / `agm066` (AGM-65)
  - NATO AAM gained `aim013` / `aim014` / `aim13` / `aim14` (AIM-9)
  - Pact Bombs gained `fab501` / `fab-500` / `fab 500` (FAB-500)
  - Pact Rockets gained `s-8` / `s8` / `80mm` (S-8 rocket)
- GunRun still goes through `CasPayloadFactory`'s runtime construction (it reflects a `CASHardpoint` into
  existence), because the game ships no gun-pod asset.

## Remaining limitations

- The MonoBehaviour serialised fields AssetRipper exported (`CASHardpoint._type` / `_ammo`,
  `CASLoadoutScriptable.HardpointPrefabs` / `Attacks`) all come out empty — those values only ever live in
  the game's runtime memory and the exported project cannot reach them. This document can therefore only
  provide a **name-level** asset inventory; it cannot provide the exact hardpoint-to-ordnance bindings.
- The genuinely executable "asset-first" path (harvesting the real loadouts / hardpoints out of memory at
  mission load) is already implemented in `CasAttackLibrary` / `CasDonorProvider`; this inventory is what
  calibrates the "guess from the name" defaults.
