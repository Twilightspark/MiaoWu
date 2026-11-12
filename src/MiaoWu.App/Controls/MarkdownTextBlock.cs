using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using MiaoWu.Core.Markdown;

namespace MiaoWu.App.Controls
{
    /// <summary>
    /// 把 Core 的 Markdown 行内 AST 渲染成 Run / Hyperlink。
    /// 只允许 http / https / mailto 链接可点击；HTML 永远按普通文本处理。
    /// </summary>
    public sealed class MarkdownTextBlock : TextBlock
    {
        public static readonly DependencyProperty InlinesSourceProperty = DependencyProperty.Register(
            nameof(InlinesSource),
            typeof(IEnumerable<MarkdownInline>),
            typeof(MarkdownTextBlock),
            new PropertyMetadata(null, OnInlinesSourceChanged));

        public MarkdownTextBlock()
        {
            TextWrapping = TextWrapping.Wrap;
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }

        public IEnumerable<MarkdownInline> InlinesSource
        {
            get { return (IEnumerable<MarkdownInline>)GetValue(InlinesSourceProperty); }
            set { SetValue(InlinesSourceProperty, value); }
        }

        private static void OnInlinesSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownTextBlock)d).Rebuild();
        }

        private void Rebuild()
        {
            Inlines.Clear();

            IEnumerable<MarkdownInline> source = InlinesSource;
            if (source == null)
            {
                return;
            }

            foreach (MarkdownInline inline in source)
            {
                if (inline == null)
                {
                    continue;
                }

                switch (inline.Kind)
                {
                    case MarkdownInlineKind.Code:
                        AddCode(inline.Text);
                        break;
                    case MarkdownInlineKind.Strong:
                        AddStyled(inline.Text, FontWeights.SemiBold, FontStyles.Normal);
                        break;
                    case MarkdownInlineKind.Emphasis:
                        AddStyled(inline.Text, FontWeights.Normal, FontStyles.Italic);
                        break;
                    case MarkdownInlineKind.Link:
                        AddLink(inline.Text, inline.Url);
                        break;
                    default:
                        Inlines.Add(new Run(inline.Text ?? string.Empty));
                        break;
                }
            }
        }

        private void AddCode(string text)
        {
            var run = new Run(text ?? string.Empty)
            {
                FontFamily = ResolveFont("Font.Family.Code", new FontFamily("Consolas")),
                FontSize = ResolveDouble("Font.InlineCode.Size", 11.5)
            };

            Brush background = ResolveBrush("Brush.CodeBackground");
            if (background != null)
            {
                run.Background = background;
            }

            Inlines.Add(run);
        }

        private void AddStyled(string text, FontWeight weight, FontStyle style)
        {
            Inlines.Add(new Run(text ?? string.Empty)
            {
                FontWeight = weight,
                FontStyle = style
            });
        }

        private void AddLink(string text, string url)
        {
            Uri uri;
            if (!TryCreateLinkUri(url, out uri))
            {
                Inlines.Add(new Run(text ?? string.Empty));
                return;
            }

            var hyperlink = new Hyperlink(new Run(text ?? string.Empty))
            {
                NavigateUri = uri,
                TextDecorations = System.Windows.TextDecorations.Underline
            };

            Brush accent = ResolveBrush("Brush.Accent");
            if (accent != null)
            {
                hyperlink.Foreground = accent;
            }

            hyperlink.RequestNavigate += OnRequestNavigate;
            Inlines.Add(hyperlink);
        }

        private static bool TryCreateLinkUri(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed))
            {
                return false;
            }

            if (parsed.Scheme != Uri.UriSchemeHttp
                && parsed.Scheme != Uri.UriSchemeHttps
                && parsed.Scheme != Uri.UriSchemeMailto)
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception)
            {
                // 打不开系统浏览器时静默失败，不打断阅读。
            }

            e.Handled = true;
        }

        private Brush ResolveBrush(string key)
        {
            object value = TryFindResource(key);
            if (value == null && Application.Current != null)
            {
                value = Application.Current.TryFindResource(key);
            }

            return value as Brush;
        }

        private FontFamily ResolveFont(string key, FontFamily fallback)
        {
            object value = TryFindResource(key);
            if (value == null && Application.Current != null)
            {
                value = Application.Current.TryFindResource(key);
            }

            return value as FontFamily ?? fallback;
        }

        private double ResolveDouble(string key, double fallback)
        {
            object value = TryFindResource(key);
            if (value == null && Application.Current != null)
            {
                value = Application.Current.TryFindResource(key);
            }

            return value is double ? (double)value : fallback;
        }
    }
}
