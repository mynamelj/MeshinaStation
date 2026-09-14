# 独立线外啮合工站

WPF / .NET Framework 4.8 / x86。程序完全独立，不引用MES项目，不启动PLC，不选择型号。原MES目录的啮合实现保持不变。

## 编译和部署

打开本目录 `MeshinaStandalone.sln`，或在仓库根目录运行：

```powershell
dotnet build MeshinaStandalone/MeshinaStandalone.csproj -c Release
```

部署时复制整个 `bin/Release/net48` 目录，运行 `MeshinaStandalone.exe`。现场需要 .NET Framework 4.8 和32位数据库引擎，默认使用已在样本上验证的 `Microsoft.Jet.OLEDB.4.0`；若使用ACE，在meshina.json填写现场已安装的32位Provider。目标电脑不需要新版.NET或SDK。

**附带配置是模板**：线别必须填写，MES地址是本机占位地址 `http://127.0.0.1:9/api/`。请先配置后启动。升级程序时保留现场的configs，避免用模板覆盖现场配置。

## 配置兼容

只读取EXE旁 `configs` 下的五个文件；不读取型号子目录或configs外的业务配置JSON。原JSON中的额外字段保留并忽略，不要求删除。

| 文件 | 使用字段 |
|---|---|
| sys.json | ListGroup中的StationNumber、Line、StationID、MachineID、OPID、Token、FixSN、Mold、RegexRule、SNCodeLen、IsSimulate |
| stationNumber.json | numberGroups中的Number、Name |
| scan.json | 原数组格式，Com、BaudRate、Parity、DataBits、StopBits、EndLine |
| api.json | ListGroup中的StationNumber、BaseUrl、FeedingCheck、CheckOutApi |
| meshina.json | 默认工位、扫码方式、MDB目录、驱动、轮询参数和字段映射 |

将原来选定型号目录中的sys.json、stationNumber.json、scan.json、api.json复制到这里的configs顶层即可，再核对实际工位内容。提供的2030/D02备份stationNumber.json列出OPXXXX1、OP2030BearingPress1、OPXXXX2、OP2030BearingPress2，它不是现成的啮合工位配置，应填入实际啮合站名称及对应sys/api条目。

程序**按工位编号匹配**：numberGroups.Number对应sys/api的StationNumber，不按数组下标匹配。可保留多个站，主界面选择当前工位，一次只运行一个工位；运行期间不能切站或改配置。meshina.json的StationNumber决定默认选中项。

扫码设置：

- 三个啮合站全部使用COM串口，不再按工位名称选择USB模式。旧配置中的ScannerMode字段会被忽略。
- 在scan.json配置现场Com号、波特率和结束符。界面保留手动输入作为调试入口；启动工位必须成功打开串口。
- ScannerIndex从0开始，选择scan.json中的一把枪，不与工位编号绑定。
- 串口沿用Com、波特率和结束符；`\\r\\n`配置会还原为回车换行。分包会拼回一条SN，多条完整条码分别处理，不拼接成一个SN。
- 本程序使用被动接收串口扫码，不使用IP/Port、触发命令OnCmd/OffCmd等其他模式字段。
- 操作员初始值来自sys.json.OPID，可在主界面修改，在本件扫码时固定到请求。没有额外的操作员JSON依赖。
- IsSimulate=true时禁止启动，避免把模拟配置误用于真实上传。

停止工位后点击“配置”，可编辑五份原格式JSON。保存前校验并备份到configs/backups，成功后重新加载；不需要重启。删除/缺失文件可在编辑窗口补全。

## 操作流程

1. 核对工位、线别、操作员、MDB目录和MES地址，启动工位。
2. 扫描产品SN。程序调用FeedingCheck，EventID取配置接口路径最后一段。
3. 界面显示进站OK后，对同一件齿轮检测并保存新的MDB。
4. 每秒扫描目录顶层。取扫码后第一个新创建的MDB，文件名不参与SN绑定；文件大小和修改时间连续3次相同后只读TJSHEET。
5. TJSHEET必须恰好一行，Fi、fii、Fr均不能为NULL。读完后复查文件大小/修改时间，变化则继续等待。
6. 自动提交MES出站，成功后允许下一件；页面保留上一件结果。

自动生产流程取**扫码后的首个新增文件**，与mdbReadTest按最后修改时间取最新文件的观察逻辑不同。扫码前已有的MDB不会被处理，也不支持只覆盖同名旧MDB的检测软件保存方式。

## 出站字段

仅提交下面三项，顺序固定；映射名可在meshina.json.ItemNames调整。

| MDB字段 | MES的DC_Info.Item |
|---|---|
| Fi | Outshaft_ToothFi1 |
| fii | Outshaft_ToothFi2 |
| Fr | Outshaft_ToothFi3 |

数值使用不带千位分隔符、小数点为`.`的字符串，0是有效值。请求沿用原MES的SNInfo/DC_Info结构；SN使用扫码值，CompList为空数组。不上传其余MDB数值列。

保留原啮合规则：MDB.Result仅显示/留存于内存测量数据，暂不参与MES判定；SNInfo.Result和各数值项Result均为PASS。字段名按本次约定映射，后续与MES确认项目含义及单位。

## 失败、终止和退出

- 文件占用、未写完整或读取失败：当前SN保持不变，后续轮询继续读取。共享读取可用，独占锁不能绕过。
- MES只把HTTP成功且Result=PASS认定为成功；FAIL认定为业务拒绝，其他结果、超时及格式错误都认定为结果未知。
- 每次请求超时30秒，无自动网络重发。失败后保留原SN和原请求数据，人工核实后点击“重试原任务”；出站重试不重新绑定别的MDB。
- “终止本件”会等待正在执行的请求结束并清空本件；已发送请求无法撤回。必须确保旧件不再保存延迟结果，再开始新件。
- 任务保存在内存，退出/重启不自动恢复。关闭未完成任务时会提示；应先核实MES状态再重做。
- 不要让原MES啮合流程与本程序同时处理同一个检测目录和工位，避免重复提交。
- 日志位于EXE旁logs/YYYYMMDD.log，包含流程、请求（Token脱敏）和响应。

## 验证

在仓库根目录执行：

```powershell
dotnet build tests/MeshinaStandalone.Tests/MeshinaStandalone.Tests.csproj -c Release
& tests/MeshinaStandalone.Tests/bin/Release/net48/MeshinaStandalone.Tests.exe 'D:\Backup\主减速齿轮_Unknown_Unknown_20260911_091435.mdb'
```

验证使用临时文件和内存HTTP处理器，不连接生产MES或PLC。覆盖真实MDB到出站JSON的完整流程、三项映射及顺序、共享读取、空值/空表/多行保护、扫码分包、配置编号匹配、并发扫码、重试、终止及迟到响应。现场串口硬件、实际MES响应和界面显示仍需联调验证。
