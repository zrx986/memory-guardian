# 动画素材与授权说明

“说的道理”动画使用原版三角脸动作，源文件来自 [Derpyu520/ottopet](https://github.com/Derpyu520/ottopet) 的“说的道理动态安全.gif”，固定提交为 `c00306cc020e27dec1ee608696432d359ca74e91`。

[原始素材页面](https://github.com/Derpyu520/ottopet/blob/c00306cc020e27dec1ee608696432d359ca74e91/%E8%AF%B4%E7%9A%84%E9%81%93%E7%90%86%E5%8A%A8%E6%80%81%E5%AE%89%E5%85%A8.gif) · [来源项目第三方声明](OTTOPET-THIRD-PARTY-NOTICES.md) · [文梧凤的原视频](https://www.bilibili.com/video/BV1JB4y1s7Dk/)

项目发布者于2026-11-01确认具有这份动图的公开分发授权，并要求随本项目发布。相关人物形象和原素材的权利仍归各自权利人；本项目的MIT代码许可证不向第三方授予额外的素材权利。

原GIF为280×280、324帧、13.5秒，SHA256：

```text
377DC0908797D02F0A7CD846C445D107462A71C1C920F58FB1BE7A6331134E0F
```

程序使用 `src/MemoryGuardian.App/Assets/Daoli.frames.png`：按原始时间线采样为128×128、12fps、162帧、8列21行的透明图集，动作完整，没有配音。图集SHA256：

```text
1A9D550CBB251D1169A44B9CD5DBE7329676098F1BEC20CBACC2F971A4CA1FE2
```

原始GIF随源码保存在 [`raw/daoli-original.gif`](../raw/daoli-original.gif)。`build/PrepareDaoliAssets`保留转换代码，先验证原文件哈希和帧数，再生成图集；普通编译使用仓库中已生成的图集，不会自动下载网络内容。

```powershell
dotnet run --project build/PrepareDaoliAssets -c Release -- raw/daoli-original.gif output/Daoli.frames.png output/asset-provenance.json
```

转换结果先写到`output/`，核对后再替换程序资源。二进制下载包只包含程序使用的图集，原始GIF随源码提供。

README中的界面截图和操作GIF由 `build/Preview.ps1`生成，使用模拟内存读数。预览中的原版三角脸同样适用本页的素材说明。
