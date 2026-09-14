# 独立线外啮合工站

WPF / .NET Framework 4.8 / x86。程序完全独立，不引用MES项目，不启动PLC，不选择型号。原MES目录的啮合实现保持不变。

## 编译和部署

打开本目录 `MeshinaStandalone.sln`，或在本项目目录运行：

```powershell
dotnet build MeshinaStandalone.csproj -c Release
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

1. 核对工位、线别、MDB目录和MES地址，启动工位。
2. 扫描产品SN，FeedingCheck返回OK后开始啮合。两个固定槽位最多容纳两件未结束任务，任务1可用时始终优先接收扫码，否则使用任务2。FeedingCheck请求中先占位，返回NG或结果未知则立即释放并允许重扫；返回OK后保持占用，同一SN在队列中不能再次进站；结束或全部终止后允许同一SN重新扫码。
3. 发现新MDB时，按扫码顺序将最早创建的未认领文件立即绑定给任务，此时不读取内容。绑定使用完整路径，不从文件名推导SN；绑定后不会因文件占用、缺失或读取失败而换成其他文件。
4. 绑定后间隔1秒，在到期后的首次轮询立即尝试读取TJSHEET并上传，不检查文件大小或修改时间是否稳定。读取失败则在后续轮询重试同一MDB，不重新等待1秒。等待不阻塞下一件扫码，两个任务的MES请求相互独立。旧配置中的MdbReadDelaySeconds和StablePollCount不再生效。
5. TJSHEET必须恰好一行，ItemNames配置的所有数值字段均不能为NULL。成功读取有效数据后立即进入出站，不再复查文件变化。
6. 提交MES出站，明确返回PASS或FAIL均结束本次任务、释放队列位置；界面用两个固定卡片分别显示SN、扫码时间、进站/出站结果、MDB及数值。两件结束结果各自保留，每次优先使用可用的任务1，否则使用任务2，未结束任务不会被覆盖。未知结果一律按NG结束，重新扫码啮合，不重发旧请求。
7. 进站OK时开始独立计时，默认100秒未发现MDB就结束任务、释放槽位，橙色提示“超时重扫”。启动前可在“等待MDB超时（秒）”输入框设置正整数，本次启动有效；持久默认值可在meshina.json.MdbWaitTimeoutSeconds配置。已经绑定MDB的任务不受此超时影响，继续等待读取和出站。
8. 点击“终止全部任务”会一起终止队列中的两件任务，等待在途操作结束后释放两个槽位，清空两张卡片的SN、进站/出站结果、MDB和数值，没有单件终止按钮。

程序没有PLC或独立的“啮合结束”信号，以新MDB出现作为可绑定的依据。设备必须按实际检测顺序创建不同名称的MDB；如果设备把第二件MDB先于第一件创建，仅靠文件元数据不能可靠区分，需增加设备任务标识或结束信号。相同创建时间按文件路径排序。

扫码前已有文件被排除，同一运行期间已绑定的MDB始终不会再次分配。只覆盖同名旧MDB的保存方式不支持。任务和认领记录保存在内存，重启后不恢复未结束任务，首次扫码时已有文件仍会被排除。

## 出站字段

读取、显示和上传均由meshina.json.ItemNames决定：键为MDB列名，值为MES项目名，按配置顺序处理。默认三项如下，可配置任意数量；未配置的列不上传。

| MDB字段 | MES的DC_Info.Item |
|---|---|
| Fi | Outshaft_ToothFi1 |
| fii | Outshaft_ToothFi2 |
| Fr | Outshaft_ToothFi3 |

数值使用不带千位分隔符、小数点为`.`的字符串，0是有效值。请求沿用原MES的SNInfo/DC_Info结构；SN使用扫码值，CompList为空数组。所有配置项均为必填，缺列、NULL或非数值时不上传部分数据，保留任务等待重试。字段名和MES项目名不能重复或为空。表格显示最多六行高度，更多项目可滚动查看。

保留原啮合规则：MDB.Result仅显示/留存于内存测量数据，暂不参与MES判定；SNInfo.Result和各数值项Result均为PASS。字段名按本次约定映射，后续与MES确认项目含义及单位。

六项配置示例（替换原ItemNames）：

```json
"ItemNames": {
  "Fi": "Midshaft_UptoothFi1",
  "fii": "Midshaft_UptoothFi2",
  "Fr": "Midshaft_UptoothFr3",
  "Fi1": "Midshaft_DowntoothFi1",
  "fii1": "Midshaft_DowntoothFi2",
  "Fr1": "Midshaft_DowntoothFr3"
}
```

停止工位后修改配置并重新启动。各工位继续使用自己的ItemNames，不需要把三项工位统一改为六项。

## 失败、终止和退出

- 文件占用、未写完整或读取失败：当前SN保持不变，后续轮询继续读取。共享读取可用，独占锁不能绕过。
- MES只把HTTP成功且Result=PASS认定为成功；FAIL认定为业务拒绝，其他结果、超时及格式错误由网关记录为结果未知，任务统一按NG结束。
- 每次请求超时30秒，无自动网络重发。FeedingCheck非OK释放槽位，出站无论OK/NG/未知都结束任务；重新扫码会创建新任务，必须重新啮合生成新MDB。
- “终止全部任务”立即禁止两件任务的后续步骤和新扫码，再等待全部在途读取/MES请求结束并清空队列；已发送请求无法撤回。必须确保旧件不再保存延迟结果，再开始新件。
- 任务保存在内存，退出/重启不自动恢复。关闭未完成任务时会提示；应先核实MES状态再重做。
- 不要让原MES啮合流程与本程序同时处理同一个检测目录和工位，避免重复提交。
- 原运行日志仍位于EXE旁logs/YYYYMMDD.log。
- 新增简易出站日志默认位于EXE旁logs/checkout/YYYYMMDD.txt（可用CheckoutLogDirectory配置），每行JSON仅包含SN、MDB完整路径和出站SNInfo数据，不包含Token等接口元数据。每次发送前追加一行，同一SN多次加工及再次扫码加工分别追加，不覆盖旧记录。日志写入失败时暂缓发送，写入恢复后继续。

## 验证

在本项目目录执行：

```powershell
dotnet build tests/MeshinaStandalone.Tests.csproj -c Release
& tests/bin/Release/net48/MeshinaStandalone.Tests.exe
```

验证使用临时文件、模拟时钟和内存MES网关，不连接生产MES或PLC。覆盖两件队列、提前绑定、1秒等待、NG结束、重复扫码、并发请求、文件读取失败、文件变化不阻塞上传、日志写入失败、未知结果按NG结束、任务1优先、独立MDB超时和全部终止。现场串口、真实MDB延迟写入顺序和实际MES仍需联调。
