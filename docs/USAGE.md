# 使用说明

## 常用操作

- **立即整理**：执行一次工作集整理，显示前后可用内存与观测变化。自动模式暂停时也可使用。
- **暂停自动整理 / 继续自动整理**：保存自动模式状态，重启工具后继续使用该设置。
- **隐藏到托盘**：主窗口消失，自动监测继续。动画停止并释放已解码图片。
- **登录后自动运行**：启用或移除本工具创建的当前用户登录任务。
- **托盘 → 退出**：结束工具。正在整理时会等待本次完成；工具不会自行反复重启。

电脑关闭或休眠时工具不运行，恢复后继续监测。自动模式至少间隔30分钟，同一轮持续高占用只尝试一次。使用率低于65%持续2分钟后解锁下一轮；手动操作不受这些自动门槛限制，但仍禁止两个整理操作同时执行。

## 首次安装和更新

解压完整发布包，运行 `MemoryGuardian.exe`。部署会申请管理员权限，将清单中的文件复制到受保护的 Program Files 目录，注册当前用户的登录任务，并启动后台实例。

更新前，从托盘退出旧版，再运行新发布包里的程序。更新保留 ProgramData 中的设置与记录。请勿直接覆盖仍在运行的 EXE/DLL。

## 命令调用

| 命令 | 作用 |
| --- | --- |
| `--status` | 返回 JSON 状态、内存读数、规则和最近结果 |
| `--probe` | 读取内存、权限及运行库位置，不整理 |
| `--clean` | 手动整理，等待并返回实际结果 |
| `--pause` / `--resume` | 暂停 / 恢复自动模式 |
| `--show` | 打开已有窗口 |
| `--stop` | 结束当前实例 |
| `--install` | 部署并设置登录启动 |
| `--background` | 后台托盘启动 |
| `--monitor-only` | 只读验证模式，不整理或写入设置 |

GUI 程序在 PowerShell 中可这样可靠地读取输出：

```powershell
$exe = Join-Path $env:ProgramFiles 'LocalMemoryTools\MemoryGuardian\MemoryGuardian.exe'
$result = Join-Path $env:TEMP 'memory-guardian-status.json'
$process = Start-Process $exe -ArgumentList '--status' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $result
Get-Content $result -Raw | ConvertFrom-Json
```

退出码：0为完成，1为失败或拒绝，2为控制连接未建立。控制入口只接受固定命令，限制当前用户、管理员与SYSTEM，并拒绝网络登录身份。

## 整理效果怎么看

窗口显示的是整理前后可用物理内存的观测变化。其他应用同时运行也会影响读数，变化可能为负。系统提交量和工作集是不同指标；应用仍持有分配时，提交量可能基本不变。

如果应用再次访问已移出的页面，占用回升是正常现象。频繁整理可能增加缺页和重新载入的开销，因此自动模式带有间隔与周期保护。

## 移除

取消“登录后自动运行”，从托盘退出，再删除 `%ProgramFiles%\LocalMemoryTools\MemoryGuardian`。历史记录在 `%ProgramData%\LocalMemoryTools\MemoryGuardian`，可按需要保留或删除。删除程序目录可能需要管理员权限。
