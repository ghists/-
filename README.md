# 事件记录仪

事件记录仪是一款本地、隐私优先的 Windows 文件变化记录器。它默认实时监听本机所有可用固定磁盘，也可切换为自定义目录；记录创建、修改、重命名、移除和可关联的移动线索，并提供 SQLite 时间线、组合搜索、文件历史、完整性状态和本地导出。

程序不记录键盘输入、剪贴板正文、屏幕截图、麦克风、聊天内容、网页正文或文件正文，也不包含网络上传功能。

## 已实现功能

- 托盘常驻，可打开、暂停、定时暂停、恢复和退出。
- 首次运行默认开启全局记录，自动覆盖所有可用固定磁盘；可在设置中关闭并改为自定义目录。
- 全局模式只建立实时监听，不在启动时递归预扫整盘，避免启动卡顿和大量磁盘读取。
- 自定义目录支持桌面、文档、下载快捷候选和任意可用路径。
- 默认只保留用户或软件路径中的变化；能够从程序目录识别时，来源会直接显示为“Codex（推定）”等软件名，识别不了时显示“用户/软件（推定）”。Windows 系统后台路径默认不入库。
- “记录 Windows 系统后台文件变化”和“记录 Microsoft Defender 防病毒隔离/保护历史记录相关变化”是两个独立开关，默认均关闭。
- 选中时间线事件后，右侧会显示“关联程序（按路径推定）”，可直接开启或停止记录该程序目录的新变化；设置会保存，且不会删除已有历史。
- 来源开关只影响保存后的新事件，不会删除已有历史；旧记录仍会按路径重新显示来源分类。
- 自动合并重复或嵌套监控根，可选择递归监控，不跟随作为根目录的链接或挂载点。
- 记录文件及目录的创建、修改、重命名、移除，以及监控根之间可关联的移动。
- 保存 Windows 文件身份；直接身份证据标为“已确认”，名称和大小启发式只标为“可能”。
- 按文件名、旧路径、新路径、事件类型、来源、目录和时间范围组合筛选；事件类型和来源都使用可保持展开的多选勾选菜单，来源列表会自动加入识别到的软件名称。
- 实时刷新会保持当前选中事件和右侧详情；即使记录暂时离开当前结果上限，观察面板也不会被新事件冲掉。
- 查看文件历史、验证当前位置，并在资源管理器中定位仍存在的对象。
- 私密目录排除；排除内容在数据库写入前过滤。
- 前台应用或正在运行的应用触发全局暂停，离开后等待 3 秒稳定时间再恢复。
- 手动暂停、15 分钟暂停、1 小时暂停、锁屏暂停和休眠暂停；暂停期间不补录。
- 修改事件按 2 秒窗口合并，避免连续保存造成时间线刷屏。
- 目录离线、权限失败、通知缓冲区溢出、内部队列溢出和暂停区间均写入“记录缺口”。
- 30 天历史和 512 MB 空间上限默认值，可在设置中调整并自动维护。
- 当前结果导出 CSV 或 JSON，默认可隐藏目录；CSV 会防止公式注入。
- 设置导入导出、当前用户自启动、单实例运行和本地健康面板。
- SQLite 使用单写入队列、批量事务、WAL 和版本迁移。

## 可信度边界

目录通知能证明观察到了路径变化，但通常不能证明是谁操作，也不能区分“删除”和“移出未监控范围”。同一监控目录内的重命名由系统直接报告；跨监控目录移动优先使用稳定文件身份关联，没有身份时只把短时间内名称和大小一致的结果标为候选。

“用户/软件”“Windows 系统”和“Microsoft Defender”来源按 Windows 保留路径、Defender 隔离区及保护历史记录相关路径推定，不等同于取得实际写入进程。Defender 目录受系统权限保护时，相关记录属于尽力观察，不替代 Windows 安全中心中的“保护历史记录”。

暂停、程序退出或通知丢失期间不会制造历史。程序会显示缺口，不承诺记录绝对完整。文件历史不是备份，当前版本不恢复旧内容，也不自动移动文件。

## 环境要求

- Windows 11 x64；Windows 10 可构建运行，但未作为正式验收范围。
- 开发构建需要 .NET 8 SDK。
- 已发布的自包含版本不需要另行安装 .NET。

## 构建和测试

```powershell
dotnet restore WindowsChangeJournal.sln --configfile NuGet.Config
dotnet build WindowsChangeJournal.sln -c Release --no-restore
dotnet run --project tests\WindowsChangeJournal.SmokeTests -c Release --no-build --no-restore
```

烟雾测试显式关闭全局模式，只在仓库的 `smoke-data` 临时目录中进行，覆盖默认配置、递归监控、创建、修改聚合、重命名、跨监控根移动、删除、组合筛选、暂停不落库、恢复记录、缺口和数据库统计。

## 发布

框架依赖版本：

```powershell
dotnet publish src\WindowsChangeJournal\WindowsChangeJournal.csproj -c Release -r win-x64 --self-contained false --no-restore -o artifacts\win-x64-framework
```

自包含版本：

```powershell
dotnet publish src\WindowsChangeJournal\WindowsChangeJournal.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\win-x64-self-contained
```

## 数据位置

```text
%LOCALAPPDATA%\WindowsChangeJournal\config.json
%LOCALAPPDATA%\WindowsChangeJournal\config.json.bak
%LOCALAPPDATA%\WindowsChangeJournal\events.db
```

卸载或删除程序不会自动删除历史数据。

## 项目结构

```text
src/WindowsChangeJournal/
  Assets/                 应用 Logo 与 Windows 多尺寸图标
  Models/                 配置、查询和事件模型
  Services/               SQLite、文件身份、监控、策略、导出和自启动
  MainWindow.*            时间线、详情、完整性和健康页面
  SettingsWindow.*        监控、排除、存储和导入导出设置
tests/WindowsChangeJournal.SmokeTests/
  Program.cs              端到端烟雾测试
```

## 视觉设计资产

- `src/WindowsChangeJournal/Assets/app-logo.png`：应用内 Logo。
- `src/WindowsChangeJournal/Assets/app-icon.ico`：可执行文件、窗口和托盘图标。
- `design/ui-concept-bananapro.png`：通过 bananapro 生成并用于本次界面改版的视觉参考。

## 后续增强边界

说明书列为 P1 的 NTFS USN 增强、受限 UI Automation 语义、跨盘候选关联和单文件受控移回没有冒充为当前能力。它们需要独立的权限、隐私和恢复安全验证后才能启用。
