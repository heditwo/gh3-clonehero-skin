namespace Gh3Skin;

// Maps the 1920 x 1080 HUD canvas to the screen (letterboxed) or to the highway camera's viewport.
internal readonly record struct HudViewport(float Scale, float OffsetX, float CenterOffsetY, float BottomOffsetY)
{
    public static HudViewport For(int width, int height)
    {
        var scale = Math.Min(width / HudCanvas.Width, height / HudCanvas.Height);
        var offsetX = (width - HudCanvas.Width * scale) / 2;
        var bottomOffsetY = height - HudCanvas.Height * scale;
        return new HudViewport(scale, offsetX, bottomOffsetY / 2, bottomOffsetY);
    }

    public static HudViewport ForHighway(int screenHeight, float cameraX, float cameraY,
        float cameraWidth, float cameraHeight, float fieldOfView)
    {
        var zoom = MathF.Tan(HighwayView.FieldOfView * MathF.PI / 360) / MathF.Tan(fieldOfView * MathF.PI / 360);
        var scale = cameraHeight / HudCanvas.Height * zoom;
        var offsetY = screenHeight - cameraY - cameraHeight / 2 - HudCanvas.Height / 2 * scale;
        return new HudViewport(scale, cameraX + cameraWidth / 2 - HudCanvas.Width / 2 * scale, offsetY, offsetY);
    }
}
