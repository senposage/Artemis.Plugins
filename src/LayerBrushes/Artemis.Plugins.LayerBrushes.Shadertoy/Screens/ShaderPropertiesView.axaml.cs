using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Artemis.Plugins.LayerBrushes.Shadertoy.Screens;

public partial class ShaderPropertiesView : ReactiveUserControl<ShaderPropertiesViewModel>
{
    public ShaderPropertiesView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            ViewModel!.DisplayPreviewImage = DisplayPreviewImage;
        });
    }

    private void InputFinished(object? sender, RoutedEventArgs e) => ViewModel?.Save();
    private void Apply_Click(object? sender, RoutedEventArgs e) => ViewModel?.Save();
    private void SavePreset_Click(object? sender, RoutedEventArgs e) => ViewModel?.SaveCurrentPreset();
    private void DeletePreset_Click(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedPreset();
    private void RemovePass_Click(object? sender, RoutedEventArgs e) => ViewModel?.RemoveSelectedPass();

    // ---- preview mouse input ----

    private float _clickX, _clickY;
    private bool  _previewDragging;

    private void PreviewMouse_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Image img || ViewModel == null) return;
        if (!e.GetCurrentPoint(img).Properties.IsLeftButtonPressed) return;

        var (sx, sy) = MapToShader(e.GetPosition(img), img);
        _clickX = sx; _clickY = sy;
        _previewDragging = true;
        ViewModel.UpdateMouse(sx, sy, true, sx, sy);
        e.Pointer.Capture(img);
    }

    private void PreviewMouse_Moved(object? sender, PointerEventArgs e)
    {
        if (sender is not Image img || ViewModel == null || !_previewDragging) return;
        var (sx, sy) = MapToShader(e.GetPosition(img), img);
        ViewModel.UpdateMouse(sx, sy, true, _clickX, _clickY);
    }

    private void PreviewMouse_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Image img || ViewModel == null) return;
        var (sx, sy) = MapToShader(e.GetPosition(img), img);
        _previewDragging = false;
        ViewModel.UpdateMouse(sx, sy, false, _clickX, _clickY);
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Maps an Avalonia pointer position (top-left origin, control space) to shader
    /// pixel coordinates (bottom-left origin, clamped to shader dimensions).
    /// Accounts for Stretch="Uniform" letterboxing within the Image control.
    /// </summary>
    private (float x, float y) MapToShader(Avalonia.Point pos, Image img)
    {
        int shaderW = ViewModel?.Width  ?? 1;
        int shaderH = ViewModel?.Height ?? 1;
        if (shaderW <= 0) shaderW = 1;
        if (shaderH <= 0) shaderH = 1;

        double ctrlW = img.Bounds.Width;
        double ctrlH = img.Bounds.Height;
        if (ctrlW <= 0 || ctrlH <= 0) return (0, 0);

        double scale   = Math.Min(ctrlW / shaderW, ctrlH / shaderH);
        double offsetX = (ctrlW - shaderW * scale) / 2.0;
        double offsetY = (ctrlH - shaderH * scale) / 2.0;

        float sx = (float)Math.Clamp((pos.X - offsetX) / scale, 0, shaderW);
        float sy = (float)Math.Clamp(shaderH - (pos.Y - offsetY) / scale, 0, shaderH); // flip Y
        return (sx, sy);
    }

    private void AddBuffer_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var type = tag switch
        {
            "BufferA" => PassType.BufferA,
            "BufferB" => PassType.BufferB,
            "BufferC" => PassType.BufferC,
            "BufferD" => PassType.BufferD,
            "Texture" => PassType.Texture,
            "Common"  => PassType.Common,
            _         => (PassType?)null
        };
        if (type.HasValue) ViewModel?.AddPass(type.Value);
    }
}
