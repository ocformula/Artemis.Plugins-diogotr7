using System;
using Artemis.Core;
using Artemis.Core.LayerBrushes;
using Artemis.Plugins.LayerBrushes.Chroma.Services;
using Artemis.Plugins.LayerBrushes.Chroma.LayerBrushes.PropertyGroups;
using RGB.NET.Core;
using RazerSdkReader.Structures;
using SkiaSharp;
using System.Collections.Generic;

namespace Artemis.Plugins.LayerBrushes.Chroma.LayerBrushes;

public class ChromaLayerBrush : PerLedLayerBrush<ChromaPropertyGroup>
{
    private readonly ChromaService _chroma;
    private readonly Dictionary<LedId, SKColor> _colors;
    private readonly SKColor[] _keyboard = new SKColor[RzDeviceType.Keyboard.GetLength()];
    private readonly object _lock;
    private bool _shouldRender;
    private bool _playingOverwatch;

    public ChromaLayerBrush(ChromaService chroma)
    {
        _chroma = chroma;
        _colors = new();
        _lock = new();
    }

    public override void EnableLayerBrush()
    {
        _chroma.MatrixUpdated += OnMatrixUpdated;
        _chroma.AppListUpdated += OnAppListUpdated;
        OnAppListUpdated(this, EventArgs.Empty);
    }

    private void OnAppListUpdated(object? sender, EventArgs e)
    {
        _shouldRender = _chroma.IsActive;
        _playingOverwatch = _chroma.CurrentApp == "Overwatch.exe";
    }

    public override void DisableLayerBrush()
    {
        _chroma.MatrixUpdated -= OnMatrixUpdated;
        _chroma.AppListUpdated -= OnAppListUpdated;
    }

    private void OnMatrixUpdated(object? sender, MatrixUpdatedEventArgs e)
    {
        var (deviceType, matrix) = e;
        var dict = DefaultChromaLedMap.GetDeviceMap(deviceType);

        lock (_lock)
        {
            for (var i = 0; i < matrix.Length; i++)
            {
                _colors[dict[i]] = matrix[i];
            }

            // The grid cells without a key all map to LedId.Invalid above, so keep the whole grid too.
            if (deviceType == RzDeviceType.Keyboard)
                matrix.CopyTo(_keyboard, 0);
        }
    }

    public override void Update(double deltaTime)
    {
    }

    public override SKColor GetColor(ArtemisLed led, SKPoint renderPoint)
    {
        if (!_shouldRender)
            return SKColor.Empty;

        lock (_lock)
        {
            if (_colors.TryGetValue(led.RgbLed.Id, out var color))
                return ProcessColor(color);

            if (GetSpreadColor(led) is { } spread)
                return ProcessColor(spread);

            //According to razer docs, chromaLink1 is the "catchall" ledId. If an LED doesn't have a mapping, use this color.
            if (Properties.UseDefaultLed && _colors.TryGetValue(LedId.LedStripe1, out var chromaLink1))
                return ProcessColor(chromaLink1);
        }

        return SKColor.Empty;
    }

    // Colour for an unmapped LED from one keyboard grid row, by the LED's horizontal position within its device.
    // Gives LED strips the keyboard's left-to-right effects in 22 steps; Chroma Link zones 2-5 were tried too,
    // but four blocks over a long strip looked crude.
    private SKColor? GetSpreadColor(ArtemisLed led)
    {
        if (!Properties.SpreadKeyboardRow)
            return null;

        var width = led.Device.RgbDevice.Size.Width;
        var position = width > 0 ? led.RgbLed.Boundary.Center.X / width : 0;
        var row = Math.Clamp(Properties.KeyboardRow.CurrentValue, 1, ChromaKeyboard.HEIGHT) - 1;
        var column = Math.Clamp((int)(position * ChromaKeyboard.WIDTH), 0, ChromaKeyboard.WIDTH - 1);
        return _keyboard[row * ChromaKeyboard.WIDTH + column];
    }

    private SKColor ProcessColor(SKColor color)
    {
        if (Properties.TransparentBlack && color == SKColors.Black)
            return SKColor.Empty;

        if (_playingOverwatch && Properties.OverwatchEnhanceColors && OverwatchColorCorrection.ColorMap.TryGetValue(color, out var correctedColor))
            return correctedColor;

        return color;
    }
}