# UI 验收测试环境记录

更新时间：2026-07-14（Asia/Shanghai）

## 拓扑

- 控制端：本机宿主机，使用提升权限 PowerShell 运行 `Rc.Cli.exe`。
- 被控端：Hyper-V VM `RcAcceptanceTarget`，Generation 2，运行中。
- 网络：外部虚拟交换机；当前 VM IPv4 为 `192.168.124.2`，Agent TCP 端口为 `43001`。
- VM 交互会话：`test` 已登录，Session ID 为 `2`。
- Hyper-V Guest Service Interface 已启用，可使用 `Copy-VMFile` 部署单文件发布物。

## 安全身份

- VM Agent 设备指纹：`BB3697D77A1ED2976212D53EB9097C279628AF623F4D9020759F60578E347ED1`
- 控制端已与该 Agent 配对。
- 本文档不记录登录密码、配对码、私钥或令牌。

## VM 部署状态

- 安装目录：`C:\Program Files\RemoteController`
- 服务：`RemoteControllerAgent`、`RemoteControllerBroker`，均为 Automatic / Running。
- 计划任务：`RemoteControllerUiAgent`（交互会话 UI Agent）；临时验收任务：`RemoteControllerUiAcceptanceTest`。
- 测试程序：`Rc.UiTestApp.exe`，窗口标题为 `RemoteController UI Acceptance Test`。
- 最新已部署 UI Agent 哈希：`252EA2E85696FF989F8844EC6D9F85C11D79542545B02D52DEA256BE6ECC0A59`。

## 测试程序覆盖范围

- UIA：Invoke、SetValue、下拉框 Expand/Collapse、下拉框第二项 `Beta`、ListBox Select。
- 树：顶层与嵌套节点全部展开，再嵌套节点、顶层节点依次收起。
- 输入：鼠标移动、左/中/右键按下/拖动/松开、滚轮上下、键盘。
- 剪贴板：写入、UI 读取和远程读回。
- 鼠标区域使用红色标记表示按下位置，并使用垂直滚动条表示滚轮位置。

## 当前实测结果

已通过：

- Invoke、SetValue。
- 下拉框展开、选中第二项 `Beta`、收起。
- SelectionItem Select。
- 双层树全部展开和依次收起。
- 鼠标移动：测试程序能收到 `MouseMove`。

未通过 / 待继续：

- `ui mouse ... button window <handle> left down` 后，测试程序未收到 WinForms `MouseDown`，没有出现红色按压标记。
- 因左键按下未通过，左键拖动/松开、滚轮、中键和右键拖动尚未完成端到端验收。
- 已分别尝试“绝对坐标移动与按键组合”和“纯按钮 SendInput”；均未产生测试控件的 MouseDown。

## 当前实现与定位线索

- 输入实现位于 `src/Rc.UiAgent/DesktopInputController.cs`，使用 Windows `user32.dll!SendInput`。
- 当前按键实现先验证光标仍处于指定窗口，再发送纯 `LEFT/MIDDLE/RIGHT DOWN/UP`。
- UI Agent 注册循环已改为对所有非取消异常重试，避免注册租约因未处理暂态异常而失效。
- 下一步：在按键前强制目标窗口前台激活，并将目标移动和按下放入同一批 `SendInput`，同时采集光标坐标、前台窗口和注入返回值，确定是否投递到错误窗口或受桌面/UIPI 限制。

## 常用验收命令

在宿主机提升的 PowerShell 中运行：

```powershell
& 'E:\RemoteController\scripts\Test-RemoteControllerUi.ps1' `
  -Endpoint '192.168.124.2:43001' `
  -Fingerprint 'BB3697D77A1ED2976212D53EB9097C279628AF623F4D9020759F60578E347ED1' `
  -StepDelayMilliseconds 250 -Verbose
```

该脚本会寻找可见的测试窗口；若需要重置验收状态，先在 VM 中重启临时任务 `RemoteControllerUiAcceptanceTest`。

## 2026-07-15 最新进度（以本节为准）

此前“左键按下未送达”的问题已修复：UI Agent 改用不包含坐标移动标志的纯 `SendInput` 按键事件。真实 VM 会话中已通过 Invoke、SetValue、下拉框/列表选择、双层树展开收起、左键按下/拖动/松开，以及滚轮上下滚动。

测试窗口现提供 **Reset acceptance state**，会复位输入框、下拉框、列表、树、滚轮、键盘框和鼠标画布，避免上一轮状态影响下一轮断言。最新脚本还纳入中键、右键的按下/拖动/松开检查，但该完整回归尚待重新跑完并记录成功结果。

键盘框使用 `ImeMode.Disable`，并以 `KeyDown(A)` 作为测试成功条件。中文输入法在普通业务窗口中可能把注入按键转换为 `ProcessKey` 或进行文本组合，因此业务自动化中的可靠文本赋值应使用 `setvalue`；不要把模拟按键后的文本内容当作跨输入法的确定性结果。
