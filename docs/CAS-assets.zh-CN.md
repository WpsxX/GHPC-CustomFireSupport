# GHPC CAS 真实资产清单（来源：AssetRipper 导出）

> 数据来源：游戏主构建 `sharedassets*.assets` 的 AssetRipper 导出清单（`path_id_map.json`，402 个文件）。
> 本文档是"CAS 到底有哪些真实武器/挂载/机型"的权威来源，用于校准 `CasAirframeCatalog` / `CasAttackLibrary` / `CasPayloadFactory` 的默认数据。

## 关键结论

- CAS 内容集中在 **`sharedassets2.assets`**（path_id_map 的 File 89，816 个资产）。
- **固定翼飞机网格**：A10、F104、F4、F4_USAF、F4_LW、MiG-21、MiG17、MiG23、MiG23_ussr、SU22、sov_airlaunched（苏制空射弹药通用件）。
- **攻击方式对应真实资产**：
  | 攻击方式 | 真实弹药网格 | 真实硬点/挂架 | 结论 |
  |---|---|---|---|
  | Bombs | mk82、FAB250、FAB501(=FAB-500) | BRU-42、BRU045、BRU046、MBD3-U2T008 | ✅ 有真实资产 |
  | Rockets | s5_rocket、s8_rocket | ub16_pod、ub32_pod、b8_pod、MARS、MARS2_pod | ✅ 有真实资产 |
  | AirToGroundMissile | **AGM65、AGM066**（AGM-65 Maverick） | LAU089/090/114/117/118/119 系列 | ✅ 有真实资产 |
  | AirToAirMissile | **AIM013、AIM014**（AIM-9 Sidewinder） | LAU 系列 | ✅ 有真实资产 |
  | GunRun | **无任何机炮硬点/吊舱** | 无 | ❌ 只能运行时构造 |

## 完整武器/挂架网格清单（sharedassets2.assets）

### 硬点/挂架（pods & racks）
- 火箭巢：`ub16_pod`、`ub32_pod`、`ub32_cone001`、`b8_pod`（在 sharedassets86.assets）、`MARS`、`MARS2_pod`
- 炸弹挂架：`BRU-42`、`BRU045`、`BRU046`、`MBD3-U2T008`
- LAU 导轨（火箭/导弹通用）：`LAU089`、`LAU090`、`LAU114`、`LAU117`、`LAU118`、`LAU119`、`LAU32`、`LAU_32`

### 弹药
- 炸弹：`mk82`、`FAB250`、`FAB501`
- 火箭：`s5_rocket`、`s8_rocket`
- 空对地导弹：`AGM65`、`AGM066`
- 空对空导弹：`AIM013`、`AIM014`

### 飞机网格
`A10`、`F104`、`F4`、`F4_USAF`、`F4_LW`、`MiG-21`、`MiG17`、`MiG23`、`MiG23_ussr`、`SU22`、`sov_airlaunched`

## 与 v1.9.2 代码的对应

- `CasPayloadFactory.SideDefaults` 的弹药关键词已按上述真实网格名校准：
  - ATGM NATO 增加 `agm65`/`agm66`/`agm066`（对应 AGM-65）
  - AAM NATO 增加 `aim013`/`aim014`/`aim13`/`aim14`（对应 AIM-9）
  - Bombs Pact 增加 `fab501`/`fab-500`/`fab 500`（FAB-500）
  - Rockets Pact 增加 `s-8`/`s8`/`80mm`（S-8 火箭）
- GunRun 仍走 `CasPayloadFactory` 运行时构造（反射造 `CASHardpoint`），因为游戏没有机炮硬点资产。

## 遗留限制

- AssetRipper 导出的 MonoBehaviour 序列化字段（`CASHardpoint._type`/`_ammo`、`CASLoadoutScriptable.HardpointPrefabs`/`Attacks`）仍为空 —— 这些数值只在游戏运行时内存里，导出工程拿不到。所以本文档只能提供**名字级**的资产清单，无法提供硬点→弹药的精确数值绑定。
- 真正可执行的"资产优先"链路（任务加载时从内存收割真实挂载/硬点）已在 `CasAttackLibrary` / `CasDonorProvider` 实现；本清单用于校准那些"名字猜"的默认值。
