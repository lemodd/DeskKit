# DeskKit 桌面工具箱

Windows 桌面效率工具箱：**截图贴图 / 剪贴板暂存架 / OCR 识文 / 局域网互传**，一个托盘常驻程序全搞定。

## 功能一览

- **剪贴板暂存架**：截图/图片/文字/文件统一收纳，屏幕边缘滑出，支持图钉贴图、磁盘缓存、自动保存
- **截图贴图**：区域/全屏截图、贴图置顶、标注、长截图（自动拼接滚动）、OCR 离线识文
- **快捷跳转**：Ctrl+G 快速跳转对话框按钮
- **粘贴即存**：资源管理器里 Ctrl+V 把剪贴板图片/文字直接存成文件
- **网络传输**：手机/电脑浏览器访问即传文件；DeskKit 设备之间可互发文字、图片、文件并聊天；首次连接需宿主确认，支持 Tailscale 跨网段

## 下载

不想自己编译的，前往 [Releases](../../releases) 下载打包好的 zip，解压即用。

**系统要求**：Windows 10/11 x64 + [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)，约 100 MB 磁盘（含 OCR 模型）。

## 从源码构建

```bash
# 需要 .NET 8 SDK（含 Windows Desktop 组件）
dotnet publish DeskKit/DeskKit.csproj -c Release -o publish
```

产物在 `publish/`：单文件 exe + 原生 DLL（onnxruntime / libSkiaSharp）+ `models\v5\` OCR 模型 + `Help.md`。

OCR 识别模型为 PP-OCRv5 中文模型（Apache-2.0，随仓库分发）；det/cls 模型由 RapidOcrNet NuGet 包在构建时自动拷入。

## 使用说明

完整帮助见 `DeskKit/Docs/Help.md`，程序内也可随时打开（托盘右键 → 帮助）。

## 常见问题

- **SmartScreen 提示"已保护你的电脑"**：程序未做代码签名，点击"更多信息 → 仍要运行"即可
- **截图热键无效**：多为 PixPin/Snipaste 等同类工具抢注了热键，关掉对方或在本程序设置里换键
- **OCR 需要联网吗**：不需要，模型随程序本地运行

## 开源组件声明

| 组件 | 许可证 |
|---|---|
| RapidOcrNet | Apache License 2.0 |
| PaddleOCR PP-OCRv5 识别模型 | Apache License 2.0 |
| ONNX Runtime | MIT License |
| QRCoder | MIT License |
| SkiaSharp | MIT License |
| .NET 8 | MIT License |

## 许可

本程序源码以 [GPL-3.0](LICENSE) 协议开源——任何基于本代码的衍生作品再发布时必须同样以 GPL-3.0 公开源码。
