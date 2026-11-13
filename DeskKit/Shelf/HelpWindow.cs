using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShotShelf
{
    /// <summary>
    /// 帮助窗口（2026-11-12）：渲染 exe 旁的 Help.md（随程序发布，改文件即更新，无需重编译）。
    /// 轻量 Markdown 渲染：#/##/### 标题、**粗体**、`行内码`、[链接](url)、- 列表、--- 分隔线、引用块。
    /// 非模态单例 ShowOrActivate；每次激活都重读文件（外部改完切回来即刷新）。
    /// </summary>
    public sealed class HelpWindow : Window
    {
        private static HelpWindow? _wnd; // 单例

        private readonly FlowDocumentScrollViewer _viewer = new();
        private readonly TextBlock _fallback = new();

        private HelpWindow(Window? owner)
        {
            Title = "帮助 · DeskKit";
            Width = 620;
            Height = 680;
            MinWidth = 460;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Icon = IconFactory.GetWpfIcon();
            if (owner != null) Owner = owner;
            Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFC));
            UseLayoutRounding = true;

            _viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _fallback.Margin = new Thickness(20);
            _fallback.TextWrapping = TextWrapping.Wrap;
            _fallback.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C));

            var grid = new Grid();
            grid.Children.Add(_viewer);
            grid.Children.Add(_fallback);
            Content = grid;

            Loaded += (_, _) => Render();
            Activated += (_, _) => { if (IsLoaded) Render(); }; // 每次切回都重读文件（外部更新即时生效）
            Closed += (_, _) => _wnd = null;
        }

        /// <summary>打开或激活帮助窗口（单例）。</summary>
        public static void ShowOrActivate(Window? owner)
        {
            if (_wnd != null) { _wnd.Activate(); return; }
            _wnd = new HelpWindow(owner);
            _wnd.Show();
        }

        /// <summary>Help.md 路径 = exe 同目录（发布时由 csproj Content 拷贝）。</summary>
        private static string HelpPath
        {
            get
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, "Help.md");
            }
        }

        private void Render()
        {
            string md;
            try { md = File.ReadAllText(HelpPath); }
            catch
            {
                _viewer.Visibility = Visibility.Collapsed;
                _fallback.Visibility = Visibility.Visible;
                _fallback.Text = $"未找到帮助文档：\n{HelpPath}\n\n它应随程序一起发布（exe 同目录的 Help.md），" +
                                 "用记事本就能编辑；也可以从源码 Docs\\Help.md 拷一份过来。";
                return;
            }
            _viewer.Visibility = Visibility.Visible;
            _fallback.Visibility = Visibility.Collapsed;

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(28, 22, 28, 22),
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            };

            bool inList = false;
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();

                // 列表连续性管理：非列表行出现就收尾（列表项之间不留额外空隙）
                if (!line.StartsWith("- ") && !line.StartsWith("* ") && inList)
                {
                    inList = false;
                }

                if (line.Length == 0)
                {
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 2) });
                    continue;
                }
                if (line.StartsWith("---") || line.StartsWith("***"))
                {
                    doc.Blocks.Add(MakeRule());
                    continue;
                }
                if (line.StartsWith("### "))
                {
                    doc.Blocks.Add(Heading(line[4..].Trim(), 14.5, 12, 4));
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    doc.Blocks.Add(Heading(line[3..].Trim(), 16, 16, 6, accent: true));
                    continue;
                }
                if (line.StartsWith("# "))
                {
                    doc.Blocks.Add(Heading(line[2..].Trim(), 20, 4, 10, accent: true));
                    continue;
                }
                if (line.StartsWith("> "))
                {
                    var p = new Paragraph { Margin = new Thickness(10, 4, 10, 4), Foreground = Gray(0x5C, 0x6B, 0xC0) };
                    p.Inlines.Add(Inlines(line[2..].Trim(), italic: true));
                    doc.Blocks.Add(p);
                    continue;
                }
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    inList = true;
                    var p = new Paragraph { Margin = new Thickness(18, 1, 8, 1) };
                    p.Inlines.Add(new Run("•  ") { Foreground = Gray(0x1E, 0x88, 0xE5), FontWeight = FontWeights.Bold });
                    p.Inlines.Add(Inlines(line[2..].Trim()));
                    doc.Blocks.Add(p);
                    continue;
                }

                var body = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
                body.Inlines.Add(Inlines(line));
                doc.Blocks.Add(body);
            }

            _viewer.Document = doc;
        }

        private static Paragraph Heading(string text, double size, double top, double bottom, bool accent = false)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, top, 0, bottom),
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Foreground = accent ? Gray(0x1E, 0x88, 0xE5) : Gray(0x33, 0x38, 0x3F),
            };
            p.Inlines.Add(Inlines(text));
            return p;
        }

        private static Block MakeRule()
        {
            var p = new Paragraph { Margin = new Thickness(0, 6, 0, 6) };
            var r = new Run(new string('─', 60)) { Foreground = Gray(0xD9, 0xDC, 0xE1), FontSize = 10 };
            p.Inlines.Add(r);
            return p;
        }

        private static Brush Gray(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

        /// <summary>行内格式：**粗体**、`行内码`、[文字](http链接)。返回一个装全部内联的 Span
        /// （InlineCollection 无公共构造器，不能直接 new）。</summary>
        private static Span Inlines(string text, bool italic = false)
        {
            var span = new Span();
            // 先按链接切，链外再处理粗体/行内码
            var linkRx = new Regex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)");
            var pos = 0;
            foreach (Match m in linkRx.Matches(text))
            {
                if (m.Index > pos) span.Inlines.Add(StyledRuns(text[pos..m.Index], italic));
                var link = new Hyperlink(new Run(m.Groups[1].Value))
                {
                    Foreground = Gray(0x1E, 0x88, 0xE5),
                    NavigateUri = new Uri(m.Groups[2].Value),
                    TextDecorations = TextDecorations.Underline,
                };
                link.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(m.Groups[2].Value) { UseShellExecute = true }); }
                    catch { }
                };
                span.Inlines.Add(link);
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) span.Inlines.Add(StyledRuns(text[pos..], italic));
            if (italic) ApplyItalic(span);
            return span;
        }

        private static void ApplyItalic(Span span)
        {
            foreach (var inline in span.Inlines)
                if (inline is Run r) r.FontStyle = FontStyles.Italic;
                else if (inline is Span s) s.FontStyle = FontStyles.Italic;
                else if (inline is Hyperlink h) h.FontStyle = FontStyles.Italic;
        }

        /// <summary>粗体/行内码分段（不含链接处理）。</summary>
        private static Span StyledRuns(string text, bool italic)
        {
            var span = new Span();
            var rx = new Regex(@"(\*\*[^*]+\*\*|`[^`]+`)");
            var pos = 0;
            foreach (Match m in rx.Matches(text))
            {
                if (m.Index > pos) span.Inlines.Add(new Run(text[pos..m.Index]));
                var token = m.Value;
                if (token.StartsWith("**"))
                    span.Inlines.Add(new Run(token[2..^2]) { FontWeight = FontWeights.Bold });
                else // `code`
                    span.Inlines.Add(new Run(token[1..^1])
                    {
                        FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                        Foreground = Gray(0x5C, 0x6B, 0xC0),
                        Background = Gray(0xEE, 0xF1, 0xFB),
                    });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) span.Inlines.Add(new Run(text[pos..]));
            return span;
        }
    }
}
