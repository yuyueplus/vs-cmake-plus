# 0.3.0 验证记录

日期：2026-09-08。当前版本是功能原型。

## 已通过

- 60 项自动检查，包括 CMake 词法处理、路径边界、并发修改检测、中文 UTF-8、脚本插入位置、配置输入过期判断、本机构建路径与缓存源码归属校验，以及新目标、批量引用修改和复杂声明拒绝。
- 真实 MSVC/CMake File API 集成：配置、读取多配置模型、新建源码、再次配置、构建、CTest。
- VS 2022 17.14 与 VS 2026 18.9 的 MSBuild 构建和独立 CMakePlus 实验配置部署。
- VS 2022 实际加载 Tools.CMakePlus 命令和工具窗口，输入示例源码及构建目录，读取 4 个配置，并显示 hello [EXECUTABLE] 目标。
- VS 2022 自动识别已打开的 hello 文件夹、windows-vs2022 Preset、已展开的 binaryDir，自动读取 4 个配置。原生 Build Preset 从 debug 切换 release 后，Release 构建配置可识别；原生全部生成成功。
- VS 2026 实际从工具菜单加载插件，无手动路径输入，自动识别 windows-vs2022 / Release 及模型。关闭文件夹后清空旧目录与模型，显示等待工作区。
- 修改示例 Presets 后，VS 自动重新配置，插件自动读取新索引。
- 安装包包含 Core、VSIX 主程序集与 Newtonsoft.Json 运行时依赖。
- 0.3 真实集成测试创建 EXECUTABLE、STATIC、SHARED 三种目标，连接父目录，批量加入中文/空格路径、去重、移除引用保留文件；重新配置后的 File API 模型与操作一致，三个目标均通过 MSVC Debug 构建。
- VS 2022 UI 实际通过向导创建 `my_target` 程序和 `ui_library` 静态库；创建程序后 Ctrl+Z 撤销父脚本连接，Ctrl+Y 重做，未保存前磁盘父脚本不变。
- VS 2022 在父脚本已有未保存中文注释时创建静态库，预览与修改均保留注释；保存后由 VS 原生 CMake 自动配置，目标树显示程序与静态库。
- UI 测试发现 VS 对纯 ASCII 脚本默认使用系统编码保存，已修复为应用编辑时设置 ITextDocument 的 UTF-8 无 BOM 保存编码。
- VS 2026 UI 实际批量加入 `中文辅助.cpp` 与 `extra_two.cpp` 到静态库；保存后严格 UTF-8 解码与无 BOM 检查通过，原生配置后目标树按实际目录显示新文件和目标归属。
- VS 2026 UI 实际移除 `extra_two.cpp` 引用，保存后脚本与 File API 都不再包含它，磁盘实体文件保留。
- 上述 UI 操作生成的工程使用原生 VS 配置结果，通过命令行 MSVC Debug 构建与 CTest；程序、静态库及中文源码均构建成功。

0.3 自动检查样例：`artifacts/tests/02bf3fa5ffc246b184276e4cd29f8023/中文 space`。
UI 操作样例：`artifacts/ui-03`，不修改原始 `samples/hello`。测试使用实验配置；最终版本号为 0.3.0。

## 尚未验收

- VS 2026 中新建源码、编辑器跳转和调试的完整操作流程。
- VS 编辑器中新建源码到断点命中的完整 UI 流程；自动集成测试不替代此项。
- 主题切换、高 DPI、多显示器与大型仓库的完整矩阵。初次界面检查发现的深色节点文字对比度问题已修正。
- 本机 VS 2022 的 CMake 全部生成未触发 Workspace IBuildService、IVsSolutionBuildManager、DTE BuildEvents 的完成回调。插件将原生构建结果标记为未提供，不能宣称已完整接入 CMake 构建生命周期。
- 多个 CMake 根目录、CMakeSettings 旧配置、WSL/SSH、工作区快速切换和大量文件变动压力测试。

仅向实验配置部署。常用 VS 配置可通过 artifacts/release/CMakePlus-0.3.0.vsix 安装；操作说明见 README。
