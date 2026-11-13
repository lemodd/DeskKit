using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;      // 2026-11-12 裁剪：Canvas/Button（其余处仍用全限定避免歧义）
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard; // WPF 版（避开 WinForms 同名类）
using DataObject = System.Windows.DataObject; // WPF 版（避开 WinForms 同名类）
using ShotShelf.Annotation;                    // 升级阶段4：注释编辑器

namespace ShotShelf
{
    /// <summary>
    /// 预览窗口（S5，2026-10-26 十二次更新支持文本）：
    /// - 图片：全尺寸显示，滚轮缩放（25% ~ 800%），中键拖动平移
    /// - 文本：只读大文本视图，可滚动、可选中复制
    /// - 底部信息栏 + 复制/另存为按钮
    /// - 升级阶段4（2026-11-02）：空格调出注释工具条，可画矩形/椭圆/箭头/画笔/文字/马赛克；
    ///   复制/另存为时合成注释（卡片缩略图与缓存保持原图不变）；背景色可配置
    /// </summary>
    public partial class PreviewWindow : Window
    {
        private readonly ShotItem _item;
        private readonly ShotStore _store;
        private readonly AppConfig _cfg;
        private double _zoom = 1.0;
        private int _rotation = 0;   // 2026-11-12 旋转：累计顺时针角度（0/90/180/270），显示与输出一致
        private AnnotationEditor? _ann;       // 注释编辑器（仅图片条目创建）
        private string _infoBase = "";        // 信息栏基础文案（注释状态后缀拼在后面）
        private bool _annSentToShelf;         // 本次标注是否已送入暂存架（防"完"重复送）

        // ---- 2026-11-05 OCR 识文（v2 简化版）：底部栏点 OCR → 直接识别整张图 → 弹结果窗 ----
        // v1 的框选覆盖层（OcrRegionOverlay/_ocrActive 及各手势守卫）已删除，旧实现见 backup_20261105_before_ocr_simplify

        public PreviewWindow(ShotItem item, ShotStore store, AppConfig cfg)
        {
            InitializeComponent();
            Icon = IconFactory.GetWpfIcon();   // 2026-11-12 统一程序图标
            _item = item;
            _store = store;
            _cfg = cfg;

            // ---- 背景色（升级阶段4）：默认中性灰，可在 config.json 改 PreviewBackgroundColor ----
            try
            {
                Background = (Brush)new BrushConverter().ConvertFromString(cfg.PreviewBackgroundColor);
            }
            catch { /* 配置坏了就用 XAML 默认色 */ }

            // ---- 文本条目（2026-10-26 十二次更新） ----
            if (item.Kind == ShotKind.Text)
            {
                // 二十五次更新：文字预览窗口缩到原来的一半（900x640 → 450x320），
                // 纯文本不需要那么大的画布，小窗口看着更省地方。
                // 2026-11-02 反馈：MinWidth=660（图片模式按钮兜底）会把文本窗撑宽 →
                // 文本分支单独放宽 MinWidth，宽度取实际显示宽 660 的 2/3 ≈ 440
                MinWidth = 300;
                Width = 440;
                Height = 320;

                var text = store.GetText(item) ?? "";
                ScrImage.Visibility = Visibility.Collapsed; // 图片区让位
                BtnResetZoom.Visibility = Visibility.Collapsed; // 缩放对文本无意义
                BtnSaveAs.Visibility = Visibility.Collapsed;    // 另存为对文本无意义
                // OCR/旋转按钮在注释工具条内（AddHostButton），文本条目不创建编辑器 → 自动隐藏
                _origText = text;            // 脏比对基准
                LoadTextDocument(text);      // 18 号大字 + 大行距，可直接编辑
                TxtPreview.Visibility = Visibility.Visible;

                // 2026-11-02 反馈：不再点保存按钮/Ctrl+Enter —— 编辑停顿 800ms 自动保存，
                // 关窗时若有未落盘的改动也立即补存（见 TxtPreview_TextChanged / OnPreviewClosed）
                _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(TextAutoSaveMs),
                };
                _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); SaveText(); };
                Closing += (_, _) =>
                {
                    // 关窗补存：定时器还没到就关了 → 立刻保存这次改动，不丢字
                    if (_autoSaveTimer.IsEnabled) { _autoSaveTimer.Stop(); SaveText(); }
                };

                // 2026-11-02 反馈：常驻信息移到标题栏，底部栏只显示临时状态提示
                TxtTitleInfo.Text = $"文本 · {text.Length} 字 · {item.CapturedAt:yyyy-MM-dd HH:mm:ss}";
                TxtInfo.Text = "";
                TxtCaption.Text = $"预览文本 · #{item.Index}";
                Title = $"预览文本 - #{item.Index}";
                return;
            }

            var bmp = store.GetFullImage(item);
            if (bmp != null)
            {
                // 初始缩放在 Loaded 里的 SizeWindowToImage 一并处理（窗口尺寸跟随图片大小）
                _zoom = 1.0;
                _rotation = 0;
                RotTf.Angle = 0;
                _baseOverride = null; // 换条目：丢弃上个条目的裁剪基准
                ApplyZoom();

                ImgPreview.Source = bmp;
                // 2026-11-02 反馈：常驻信息移到标题栏（底部栏只留临时状态提示）
                _infoBase = $"{bmp.PixelWidth}x{bmp.PixelHeight} · {item.CapturedAt:yyyy-MM-dd HH:mm:ss}";
                TxtTitleInfo.Text = _infoBase;
                TxtInfo.Text = "";

                // ---- 升级阶段4：注释编辑器（与图共用 ImgHost 的 LayoutTransform） ----
                InitAnnotation(bmp);

                // 2026-11-05 v2：OCR 改为一键识别，v1 的 OcrRegionOverlay 初始化已删除

                // 快捷键：Ctrl+Z/Y 撤销重做 / Esc 关窗
                KeyDown += OnPreviewKeyDown;

                // 二十次更新：窗口首次显示后再居中一次（此时视口尺寸才有效）
                // 2026-11-02 反馈：窗口尺寸跟随图片大小（保证标题栏/底部栏按钮完整显示）
                Loaded += (_, __) => { SizeWindowToImage(bmp); CenterImage(); };
                SizeChanged += (_, __) => CenterImage(); // 拖动窗口大小后也回到正中
            }
            else
            {
                TxtTitleInfo.Text = "图片数据丢失";
                TxtInfo.Text = "";
            }

            TxtCaption.Text = $"预览 · #{item.Index}";
            Title = $"预览 - #{item.Index}";
        }

        /// <summary>初始化注释编辑器 + 工具条宿主按钮（OCR/旋转/裁剪）。
        /// 2026-11-12 提方法：裁剪应用后要用新基准图重建编辑器，直接复用</summary>
        private void InitAnnotation(BitmapSource baseBmp)
        {
            _ann = new AnnotationEditor(baseBmp);
            ImgHost.Children.Add(_ann);
            AnnToolbarHost.Content = _ann.ToolbarView; // 工具条在底部栏内（与按钮同一行）
            AnnToolbarHost.Visibility = Visibility.Visible;
            _ann.SetToolbarVisible(true);
            _ann.RequestToggleToolbar += SendAnnotatedToShelf;   // "送"按钮
            _ann.StateChanged += () => { _annSentToShelf = false; RefreshInfo(); }; // 改动后允许再送
            // 宿主按钮（与注释工具同排同风格）
            _ann.AddHostButton(DeskKit.Icons.Ocr, "OCR 识文：识别整张图（含注释），弹出结果窗",
                BtnOcr_Click, stroke: 1.0);
            // 旋转图标是 stroke 轮廓型 SVG（fill=none），必须 strokeOnly 渲染
            _ann.AddHostButton(DeskKit.Icons.RotateCw, "顺时针旋转 90°", BtnRotate_Click, strokeOnly: true);
            _cropBtn = _ann.AddHostButton(DeskKit.Icons.Crop, "裁剪：拖出区域，框内可拖动调整位置，双击/回车应用，Esc 取消", BtnCrop_Click);
        }

        // ================= 裁剪（2026-11-12） =================

        private bool _cropMode;                // 裁剪模式中
        private bool _cropHasSel;              // 已拖出有效选框（待回车/再点应用）
        private Point _cropStart;
        private int _cropDrag;                 // 0 无 / 1 画新框 / 2 移动选框（2026-11-12 框内拖动）
        private Point _cropMoveStart;
        private Rect _cropMoveOrig;
        private Canvas? _cropLayer;            // 覆盖层：透明背景拦截注释层鼠标，十字光标
        private System.Windows.Shapes.Rectangle? _cropSel;
        private Button? _cropBtn;
        private BitmapSource? _baseOverride;   // 裁剪后的新基准图（缓存原图不变，预览内所有输出用它）

        private void BtnCrop_Click(object sender, RoutedEventArgs e)
        {
            if (!_cropMode) EnterCrop();
            else if (_cropHasSel) ApplyCrop();  // 再点裁剪 = 应用
            else ExitCrop();
        }

        private void EnterCrop()
        {
            if (_cropLayer != null) return;
            var baseBmp = _baseOverride ?? _store.GetFullImage(_item);
            if (baseBmp == null) return;

            _cropMode = true;
            _cropHasSel = false;
            _cropDrag = 0;
            _cropSel = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }),
                Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                Visibility = Visibility.Collapsed,
            };
            _cropLayer = new Canvas
            {
                Background = Brushes.Transparent, // 非 null Background 才有命中测试：拦截注释层鼠标
                Cursor = Cursors.Cross,
                Width = baseBmp.Width, Height = baseBmp.Height, // DIP 尺寸 = 图的自然尺寸
            };
            _cropLayer.Children.Add(_cropSel);
            _cropLayer.MouseLeftButtonDown += Crop_MouseDown;
            _cropLayer.MouseMove += Crop_MouseMove;
            _cropLayer.MouseLeftButtonUp += Crop_MouseUp;
            ImgHost.Children.Add(_cropLayer); // 加在 ImgHost 最上层 → 随缩放/旋转变换，坐标恒为图的自然坐标

            if (_cropBtn != null) _cropBtn.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x1E, 0x88, 0xE5));
            TxtInfo.Text = "裁剪：拖出区域 → 回车应用，Esc 取消";
        }

        private void ExitCrop()
        {
            if (_cropLayer == null) return;
            ImgHost.Children.Remove(_cropLayer);
            _cropLayer = null;
            _cropSel = null;
            _cropMode = false;
            _cropHasSel = false;
            _cropDrag = 0;
            if (_cropBtn != null) _cropBtn.Background = Brushes.Transparent;
            TxtInfo.Text = "";
        }

        private void Crop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_cropLayer == null || _cropSel == null) return;
            var p = e.GetPosition(_cropLayer);

            // 2026-11-12 双击确认裁剪（有选框时）
            if (e.ClickCount == 2)
            {
                if (_cropHasSel) ApplyCrop();
                e.Handled = true;
                return;
            }

            if (_cropHasSel && HitCropSel(p))
            {
                // 2026-11-12 框内按下：拖动整个选框
                _cropDrag = 2;
                _cropMoveStart = p;
                _cropMoveOrig = GetCropRect();
            }
            else
            {
                _cropDrag = 1;
                _cropStart = p;
                _cropHasSel = false;
                Canvas.SetLeft(_cropSel, p.X);
                Canvas.SetTop(_cropSel, p.Y);
                _cropSel.Width = 0;
                _cropSel.Height = 0;
                _cropSel.Visibility = Visibility.Visible;
            }
            _cropLayer.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>选框命中（含边线 3px 余量）</summary>
        private bool HitCropSel(Point p)
        {
            var r = GetCropRect();
            r.Inflate(3, 3);
            return r.Contains(p);
        }

        private void Crop_MouseMove(object sender, MouseEventArgs e)
        {
            if (_cropLayer == null || _cropSel == null) return;
            var p = e.GetPosition(_cropLayer);

            if (_cropDrag == 0) // 悬停光标：框内=可移动，框外=可新画
            {
                _cropLayer.Cursor = (_cropHasSel && HitCropSel(p)) ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            if (!_cropLayer.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;

            if (_cropDrag == 2)
            {
                // 平移选框（clamp 到图层内）
                double dx = p.X - _cropMoveStart.X, dy = p.Y - _cropMoveStart.Y;
                double x = Math.Max(0, Math.Min(_cropMoveOrig.X + dx, _cropLayer.Width - _cropMoveOrig.Width));
                double y = Math.Max(0, Math.Min(_cropMoveOrig.Y + dy, _cropLayer.Height - _cropMoveOrig.Height));
                Canvas.SetLeft(_cropSel, x);
                Canvas.SetTop(_cropSel, y);
            }
            else SetCropSel(p);
        }

        private void Crop_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_cropLayer == null || _cropSel == null) return;
            if (_cropLayer.IsMouseCaptured) _cropLayer.ReleaseMouseCapture();
            int drag = _cropDrag;
            _cropDrag = 0;

            if (drag == 2) // 移动结束，选框保持
            {
                var r = GetCropRect();
                TxtInfo.Text = $"裁剪 {(int)r.Width}×{(int)r.Height} px → 双击/回车应用，Esc 取消";
                e.Handled = true;
                return;
            }

            SetCropSel(e.GetPosition(_cropLayer));
            var rr = GetCropRect();
            _cropHasSel = rr.Width >= 4 && rr.Height >= 4;
            TxtInfo.Text = _cropHasSel
                ? $"裁剪 {(int)rr.Width}×{(int)rr.Height} px → 双击/回车应用，Esc 取消"
                : "区域太小，重新拖或 Esc 取消";
            if (!_cropHasSel) _cropSel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void SetCropSel(Point p)
        {
            double x = Math.Min(_cropStart.X, p.X), y = Math.Min(_cropStart.Y, p.Y);
            Canvas.SetLeft(_cropSel!, x);
            Canvas.SetTop(_cropSel!, y);
            _cropSel!.Width = Math.Abs(_cropStart.X - p.X);
            _cropSel.Height = Math.Abs(_cropStart.Y - p.Y);
        }

        private Rect GetCropRect()
        {
            double x = Canvas.GetLeft(_cropSel!), y = Canvas.GetTop(_cropSel!);
            return new Rect(x, y, _cropSel!.Width, _cropSel.Height);
        }

        /// <summary>应用裁剪：对"当前基准图+注释"合成图裁选框（含随后旋转方向跟随所见），
        /// 结果成为新基准并重建注释层——可连续多次裁剪。缓存/卡片原图不受影响</summary>
        private void ApplyCrop()
        {
            var baseBmp = _baseOverride ?? _store.GetFullImage(_item);
            if (baseBmp == null || _cropSel == null) return;
            var comp = (_ann != null && _ann.HasAnnotations) ? _ann.Composite(baseBmp) : baseBmp;
            var r = GetCropRect();
            ExitCrop();

            // DIP → 像素（按位图 DPI 比例），clamp 到图内
            double sx = comp.PixelWidth / comp.Width, sy = comp.PixelHeight / comp.Height;
            int x = (int)Math.Round(r.X * sx), y = (int)Math.Round(r.Y * sy);
            int w = (int)Math.Round(r.Width * sx), h = (int)Math.Round(r.Height * sy);
            x = Math.Max(0, Math.Min(x, comp.PixelWidth - 1));
            y = Math.Max(0, Math.Min(y, comp.PixelHeight - 1));
            w = Math.Max(1, Math.Min(w, comp.PixelWidth - x));
            h = Math.Max(1, Math.Min(h, comp.PixelHeight - y));

            BitmapSource cropped = new System.Windows.Media.Imaging.CroppedBitmap(comp, new Int32Rect(x, y, w, h));
            if (_rotation != 0) // 用户看着旋转后的图拖的框 → 旋转进结果，随后角度归零（所见即所得）
            {
                var rot = new System.Windows.Media.Imaging.TransformedBitmap(cropped, new RotateTransform(_rotation));
                rot.Freeze();
                cropped = rot;
            }
            cropped.Freeze();

            // 重建：新基准 + 全新注释层（旧注释已合成进新图）
            ImgHost.Children.Remove(_ann);
            _baseOverride = cropped;
            _rotation = 0;
            RotTf.Angle = 0;
            ImgPreview.Source = cropped;
            InitAnnotation(cropped);
            ApplyZoom();
            SizeWindowToImage(cropped);
            _infoBase = $"{cropped.PixelWidth}x{cropped.PixelHeight} · 已裁剪";
            TxtTitleInfo.Text = _infoBase;
            // 2026-11-12 二修（用户反馈"裁剪完的图不知道去哪儿了"）：
            // 裁剪结果作为新条目送入暂存架（同"送"按钮的官方入口），预览内继续编辑不受影响
            try
            {
                DeskKit.App.AddImageToShelf(cropped);
                TxtInfo.Text = $"已裁剪 {cropped.PixelWidth}×{cropped.PixelHeight}，新图已进暂存架";
                DeskKit.App.Notifier($"裁剪完成 {cropped.PixelWidth}×{cropped.PixelHeight}，已进暂存架");
            }
            catch (Exception ex)
            {
                TxtInfo.Text = $"已裁剪，但进暂存架失败: {ex.Message}";
            }
        }

        // ================= 文本编辑（升级阶段5，2026-11-02） =================

        private string _origText = "";        // 打开时的原文（脏比对基准，换行统一为 \n）
        // 2026-11-02 反馈：自动保存参数（停顿多久落盘）
        private const int TextAutoSaveMs = 800;
        private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;

        /// <summary>
        /// 文本内容装入预览（2026-11-02 反馈：18 号字 + 行距 32）。
        /// 用 RichTextBox 是因为 WPF 的 TextBox 不支持 LineHeight；
        /// 多行文本拆成 Run + LineBreak，保证行距样式和回读一致。
        /// </summary>
        private void LoadTextDocument(string text)
        {
            var doc = new System.Windows.Documents.FlowDocument();
            doc.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, Segoe UI");
            doc.FontSize = 18;
            var p = new System.Windows.Documents.Paragraph
            {
                LineHeight = 32,
                LineStackingStrategy = System.Windows.LineStackingStrategy.BlockLineHeight,
            };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) p.Inlines.Add(new System.Windows.Documents.LineBreak());
                p.Inlines.Add(new System.Windows.Documents.Run(lines[i]));
            }
            doc.Blocks.Add(p);
            TxtPreview.Document = doc;
        }

        /// <summary>从编辑器读回纯文本：末段自带换行裁掉，\r\n 统一成 \n（与缓存/比对基准一致）</summary>
        private string EditorText
        {
            get
            {
                var range = new System.Windows.Documents.TextRange(
                    TxtPreview.Document.ContentStart, TxtPreview.Document.ContentEnd);
                var t = range.Text.Replace("\r\n", "\n");
                return t.EndsWith("\n") ? t[..^1] : t;
            }
        }

        /// <summary>内容变化：新段落补行距样式；有改动 → 重启 800ms 倒计时自动保存</summary>
        private void TxtPreview_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_item.Kind != ShotKind.Text) return;
            // Enter 新建的段落不继承行距，统一补齐（字号/字体由 FlowDocument 级继承）
            foreach (var p in TxtPreview.Document.Blocks.OfType<System.Windows.Documents.Paragraph>())
            {
                if (double.IsNaN(p.LineHeight))
                {
                    p.LineHeight = 32;
                    p.LineStackingStrategy = System.Windows.LineStackingStrategy.BlockLineHeight;
                }
            }
            // 自动保存：每次改动重置倒计时，停顿 800ms 落盘（连续打字不会每键写一次盘）
            if (EditorText != _origText)
            {
                _autoSaveTimer?.Stop();
                _autoSaveTimer?.Start();
            }
            else
            {
                _autoSaveTimer?.Stop(); // 改回了原文，不用存
            }
        }

        /// <summary>保存：超长拒绝；写回 ShotStore（卡片+缓存），失败仅提示</summary>
        private void SaveText()
        {
            var text = EditorText;
            if (text == _origText) return;

            if (text.Length > _cfg.MaxTextLength)
            {
                TxtInfo.Text = $"超过上限 {_cfg.MaxTextLength} 字（当前 {text.Length} 字），未保存";
                return;
            }

            bool diskOk = _store.UpdateText(_item, text);
            _origText = text; // 内存已更新，无论写盘成败都以此为新基准（避免重复保存）
            TxtInfo.Text = diskOk
                ? $"#{_item.Index} · 文本 · {text.Length} 字 · 已自动保存"
                : "内存已更新，但写缓存失败（磁盘只读或被占用）";
        }

        // ================= 注释（升级阶段4） =================

        /// <summary>
        /// 把"原图+注释"合成图作为新条目送入暂存架（走指纹去重，卡片/缓存原图不动）。
        /// 触发点：工具条上的"送"按钮。无标注时仅提示。
        /// </summary>
        private void SendAnnotatedToShelf()
        {
            if (_ann == null || !_ann.HasAnnotations)
            {
                TxtInfo.Text = "还没有标注，先画点什么吧";
                return;
            }
            if (_annSentToShelf)
            {
                TxtInfo.Text = "这轮标注已送过，继续改动后可再送";
                return;
            }
            try
            {
                var final = FinalImage();
                if (final == null) return;
                DeskKit.App.AddImageToShelf(final);
                _annSentToShelf = true;
                TxtInfo.Text = "已把标注图送入暂存架（新条目，原图保留）";
            }
            catch (Exception ex)
            {
                TxtInfo.Text = $"送入暂存架失败: {ex.Message}";
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_item.Kind != ShotKind.Image || _ann == null) return;
            // 2026-11-12 裁剪模式：回车应用 / Esc 取消（优先于关窗）
            if (_cropMode)
            {
                if (e.Key == Key.Enter && _cropHasSel) ApplyCrop();
                else if (e.Key == Key.Escape) ExitCrop();
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    break;
                case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                    _ann.Undo();
                    e.Handled = true;
                    break;
                case Key.Y when Keyboard.Modifiers == ModifierKeys.Control:
                    _ann.Redo();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>信息栏刷新：注释状态提示已按用户要求取消显示（2026-11-02），仅清空临时状态</summary>
        private void RefreshInfo()
        {
            TxtInfo.Text = "";
        }

        /// <summary>合成最终图：有注释 → 原图 + 注释；无注释 → 原图（卡片/缓存不受影响）。
        /// 2026-11-12 旋转：末尾按 _rotation 顺时针旋转（90° 倍数无损），复制/另存/OCR 输出与显示一致</summary>
        private BitmapSource FinalImage()
        {
            // 2026-11-12 裁剪：有裁剪基准用之（含已合成注释/旋转方向），否则用缓存原图
            var bmp = _baseOverride ?? _store.GetFullImage(_item);
            if (bmp == null) return null!;
            if (_ann != null && _ann.HasAnnotations) bmp = _ann.Composite(bmp);
            if (_rotation != 0)
            {
                var rbmp = new System.Windows.Media.Imaging.TransformedBitmap(
                    bmp, new RotateTransform(_rotation));
                rbmp.Freeze();
                bmp = rbmp;
            }
            return bmp;
        }

        // ---- 缩放 ----
        private void ScrImage_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 升级阶段4：注释工具选中时滚轮禁用缩放（避免绘制中形状视觉跳动，同贴图先例）
            if (_ann != null && _ann.SelectedTool != AnnTool.None)
            {
                e.Handled = true;
                return;
            }
            // Ctrl+滚轮 或 纯滚轮 都缩放（预览窗口里滚轮没别的用途）
            double step = e.Delta > 0 ? 1.2 : 1 / 1.2;
            _zoom = Math.Clamp(_zoom * step, 0.25, 8.0);
            ApplyZoom();
            e.Handled = true;
        }

        /// <summary>
        /// 应用缩放，然后把图片对到窗口正中（二十次更新）。
        /// 用 LayoutTransform 改的是"布局尺寸"，ScrollViewer 的范围要等布局跑完才更新，
        /// 所以居中必须推迟到 Loaded 优先级再算，否则算的是旧范围。
        /// </summary>
        private void ApplyZoom()
        {
            ZoomTf.ScaleX = _zoom;
            ZoomTf.ScaleY = _zoom;
            Dispatcher.BeginInvoke(new Action(CenterImage),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>滚动到正中：偏移 = (内容尺寸 - 视口尺寸) / 2；内容比视口小则偏移 0（自然居中）</summary>
        private void CenterImage()
        {
            ScrImage.UpdateLayout();

            double h = (ScrImage.ExtentWidth - ScrImage.ViewportWidth) / 2;
            double v = (ScrImage.ExtentHeight - ScrImage.ViewportHeight) / 2;

            ScrImage.ScrollToHorizontalOffset(h > 0 ? h : 0);
            ScrImage.ScrollToVerticalOffset(v > 0 ? v : 0);
        }

        // ---- 2026-11-12 旋转：顺时针 90°/次 ----
        // 显示走 ImgHost 的 RotTf（图 + 注释层一起转，对齐关系不变），
        // 输出走 FinalImage 的 TransformedBitmap（复制/另存/OCR 拿到同样的旋转结果）。
        // 90° 倍数是像素无损变换，不用担心画质。
        private void BtnRotate_Click(object sender, RoutedEventArgs e)
        {
            _rotation = (_rotation + 90) % 360;
            RotTf.Angle = _rotation;
            ApplyZoom();   // 旋转后布局包围盒变化（横竖图互换），重新居中
        }

        // ---- 2026-11-02 反馈：窗口默认尺寸跟随图片大小 ----
        // 原固定 900x640，小图显得空旷。现在：窗口 = 图片自然尺寸 + 标题栏/底部栏，
        // 超出屏幕工作区时按比例缩小；宽度受 MinWidth（660）兜底，保证底部栏所有按钮完整。
        private void SizeWindowToImage(System.Windows.Media.Imaging.BitmapSource bmp)
        {
            // DPI 缩放（DIP ↔ 物理px）
            double dpi = 1.0;
            var ps = System.Windows.PresentationSource.FromVisual(this);
            if (ps?.CompositionTarget != null) dpi = ps.CompositionTarget.TransformToDevice.M11;
            if (dpi <= 0) dpi = 1.0;

            // 当前所在显示器的工作区（物理px → DIP），留 24px 余量
            var wa = System.Windows.Forms.Screen.FromHandle(
                new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
            double maxW = wa.Width / dpi - 24;
            double maxH = wa.Height / dpi - 24;

            // 窗口自占尺寸：标题栏 + 底部栏实测高度 + 非客户区 2px 左右
            double chromeW = 2;
            double chromeH = BdTitle.ActualHeight + BdBottom.ActualHeight + 2;

            double iw = bmp.PixelWidth, ih = bmp.PixelHeight; // 96dpi 下 WPF 图片 1 像素 = 1 DIP
            const double vPad = 64; // 2026-11-02 反馈：小图时上下也留一些空（含在窗口高度里）
            double scale = Math.Min(1.0, Math.Min(maxW / (iw + chromeW), maxH / (ih + chromeH + vPad))); // 不放大只缩小

            double winW = Math.Max(MinWidth, (iw + chromeW) * scale);  // 宽度下限 = 按钮显示完整
            double winH = Math.Clamp(ih * scale + chromeH + vPad, MinHeight, maxH);

            Width = Math.Round(winW);
            Height = Math.Round(winH);
            _zoom = scale;
            ApplyZoom();
        }

        private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            _zoom = 1.0;
            ApplyZoom();
        }

        // ================= 2026-11-05 OCR 识文（v2 简化版：一键识别 → 结果弹窗） =================

        /// <summary>底部栏「OCR 识文」按钮：清注释选中 → 直接识别整张图（FinalImage 含注释，所见即所识）→ 结果弹窗</summary>
        private void BtnOcr_Click(object sender, RoutedEventArgs e)
        {
            if (_item.Kind != ShotKind.Image) return;
            if (_ann != null) _ann.SelectedTool = AnnTool.None;   // 关注释工具命中
            var bmp = FinalImage();
            if (bmp == null) return;
            DeskKit.Ocr.OcrService.RecognizeToWindowAsync(bmp, this);   // 2026-11-05：提示条居中预览窗
        }

        // ---- 拖动平移（2026-10-26 中键新增；2026-11-11 左键同功能） ----
        // 图片超出窗口时，按住中键（或左键）拖动即可平移查看(等价于同时拖动两条滚动条)。
        // 左键平移仅在注释工具未激活（SelectedTool==None）且图片超窗时生效，不影响注释绘制；
        // Preview 层 Handled 会阻断注释控件收到该次左键，实现互斥。
        private bool _isPanning;
        private System.Windows.Point _panStart;
        private double _startH, _startV;

        private void ScrImage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            bool isPanButton =
                e.ChangedButton == MouseButton.Middle ||
                (e.ChangedButton == MouseButton.Left &&
                 (_ann == null || _ann.SelectedTool == AnnTool.None) &&
                 (ScrImage.ScrollableWidth > 0 || ScrImage.ScrollableHeight > 0));
            if (!isPanButton) return;
            _isPanning = true;
            _panStart = e.GetPosition(ScrImage);
            _startH = ScrImage.HorizontalOffset;
            _startV = ScrImage.VerticalOffset;
            ScrImage.CaptureMouse(); // 拖出窗口范围也持续跟踪
            e.Handled = true;
        }

        private void ScrImage_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(ScrImage);
            // 反向补偿:内容跟着手走 = 滚动条往相反方向滚
            ScrImage.ScrollToHorizontalOffset(_startH - (pos.X - _panStart.X));
            ScrImage.ScrollToVerticalOffset(_startV - (pos.Y - _panStart.Y));
            e.Handled = true;
        }

        private void ScrImage_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            _isPanning = false;
            ScrImage.ReleaseMouseCapture();
            e.Handled = true;
        }

        // ---- 复制 / 另存为 / 关闭 ----
        /// <summary>
        /// 预览窗的复制:同样要防回环。
        /// 抑制标志放在 MainWindow(监听器所在处),通过属性通知它暂时忽略。
        /// 升级阶段4：有注释时复制合成图（原 MiniPix 复制语义）。
        /// </summary>
        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            // ---- 文本条目（2026-10-26 十二次更新） ----
            if (_item.Kind == ShotKind.Text)
            {
                var text = _store.GetText(_item);
                if (text == null) { TxtInfo.Text = "文本内容丢失"; return; }
                try
                {
                    if (Owner is MainWindow ownerT) ownerT.SuppressCapture = true;

                    var data = new DataObject();
                    data.SetText(text);
                    global::DeskKit.App.ClipboardSetWithRetry(data); // 2026-11-12：带重试

                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() => { }));

                    TxtInfo.Text = $"已复制 {text.Length} 字到剪贴板";
                }
                catch (Exception ex)
                {
                    TxtInfo.Text = $"复制失败: {ex.Message}";
                }
                finally
                {
                    if (Owner is MainWindow ownerT2) ownerT2.SuppressCapture = false;
                }
                return;
            }

            var bmp = FinalImage();
            if (bmp == null) return;
            try
            {
                var owner = Owner as MainWindow;
                if (owner != null) owner.SuppressCapture = true;

                var dataP = new DataObject(); // 2026-11-12：Clipboard.SetImage 换 DataObject+重试
                dataP.SetImage(bmp);
                global::DeskKit.App.ClipboardSetWithRetry(dataP);

                // 排空消息队列,让迟到的 WM_CLIPBOARDUPDATE 先被抑制掉
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => { }));

                TxtInfo.Text = "已复制到剪贴板（含注释）";
            }
            catch (Exception ex)
            {
                TxtInfo.Text = $"复制失败: {ex.Message}";
            }
            finally
            {
                if (Owner is MainWindow owner2) owner2.SuppressCapture = false;
            }
        }

        /// <summary>另存为 PNG（升级阶段4新增）：有注释时保存合成图，缓存原图不变</summary>
        private void BtnSaveAs_Click(object sender, RoutedEventArgs e)
        {
            var bmp = FinalImage();
            if (bmp == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png",
                FileName = $"DeskKit_preview_{_item.Index}.png",
                DefaultExt = ".png",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                MiniPix.Output.SaveUtil.SavePng(bmp, dlg.FileName);
                TxtInfo.Text = "已保存: " + dlg.FileName;
            }
            catch (Exception ex)
            {
                TxtInfo.Text = $"保存失败: {ex.Message}";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ================= 自绘标题栏（2026-11-02 UI 美化） =================

        /// <summary>标题栏按住拖动窗口；双击切换最大化/还原</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { ToggleMaximize(); return; }
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* 拖动中重复调用等，忽略 */ }
            }
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMax_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        /// <summary>最大化/还原切换，并同步标题栏按钮字形（最大化 ▢ / 还原 ❐）</summary>
        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            bool max = WindowState == WindowState.Maximized;
            if (BtnMax.Template.FindName("Glyph", BtnMax) is System.Windows.Controls.TextBlock g)
                g.Text = max ? "\uE923" : "\uE922";
        }
    }
}
