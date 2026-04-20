using System;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.Screens;

/// <summary>
/// AvaloniaEdit <see cref="TextEditor"/> pre-configured for GLSL editing.
/// Exposes a bindable <see cref="Text"/> <see cref="StyledProperty{T}"/> so it
/// can be used with <c>{CompiledBinding}</c> in DataTemplates.
/// </summary>
public class GlslEditor : TextEditor
{
    private bool _updating;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<GlslEditor, string>(nameof(Text), defaultValue: string.Empty,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Bindable text content — two-way synced with the inner TextDocument.</summary>
    public new string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // Tracks whether we have already attempted to register AvaloniaEdit styles.
    private static bool _stylesEnsured;

    /// <summary>
    /// Adds AvaloniaEdit's ControlTheme/styles to <see cref="Application.Current"/> if they
    /// are not already present.  This is necessary when the host application has not called
    /// UseAvaloniaEdit() — without it the TextEditor control has no visual template and is
    /// completely invisible.
    ///
    /// The avares:// URI is resolvable at runtime because AvaloniaEdit.dll is already loaded
    /// into the host process; the build-time XAML compiler failure that caused us to remove the
    /// StyleInclude does not affect runtime asset resolution.
    /// </summary>
    private static void EnsureAvaloniaEditStyles()
    {
        if (_stylesEnsured) return;
        _stylesEnsured = true;

        try
        {
            var app = Application.Current;
            if (app == null) return;

            var uri = new Uri("avares://AvaloniaEdit/AvaloniaEdit.xaml");

            // Skip if already loaded (e.g. host called UseAvaloniaEdit())
            if (app.Styles.OfType<StyleInclude>().Any(s => s.Source == uri))
                return;

            app.Styles.Add(new StyleInclude(new Uri("avares://AvaloniaEdit"))
            {
                Source = uri
            });
        }
        catch { /* best-effort — editor degrades to unstyled but won't crash */ }
    }

    public GlslEditor()
    {
        EnsureAvaloniaEditStyles();

        ShowLineNumbers           = true;
        SyntaxHighlighting        = GlslHighlighting.Definition;
        FontFamily                = new FontFamily("Consolas, Courier New, monospace");
        FontSize                  = 13;
        Options.EnableHyperlinks         = false;
        Options.EnableEmailHyperlinks    = false;
        Options.ConvertTabsToSpaces      = false;
        Options.IndentationSize          = 4;
        Options.HighlightCurrentLine     = true;

        Document = new TextDocument();
    }

    // Subscribe/unsubscribe from Document.TextChanged when the Document property changes.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Document replaced — rewire TextChanged subscription
        if (change.Property == DocumentProperty)
        {
            if (change.OldValue is TextDocument old) old.TextChanged -= OnDocumentTextChanged;
            if (change.NewValue is TextDocument @new)
            {
                @new.TextChanged += OnDocumentTextChanged;
                // Sync our Text property to whatever the new doc already contains
                if (!_updating)
                {
                    _updating = true;
                    try { SetCurrentValue(TextProperty, @new.Text); }
                    finally { _updating = false; }
                }
            }
        }

        // Text (ViewModel → editor): push new value into the document
        if (change.Property == TextProperty && !_updating)
        {
            _updating = true;
            try
            {
                string newText = change.GetNewValue<string>() ?? string.Empty;
                if (Document == null)
                    Document = new TextDocument(newText);
                else if (Document.Text != newText)
                    Document.Text = newText;
            }
            finally { _updating = false; }
        }
    }

    // editor → ViewModel: push document content back to Text property
    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_updating) return;
        _updating = true;
        try { SetCurrentValue(TextProperty, Document?.Text ?? string.Empty); }
        finally { _updating = false; }
    }
}
