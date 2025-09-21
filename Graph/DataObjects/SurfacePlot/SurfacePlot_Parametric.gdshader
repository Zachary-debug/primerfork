private static ImageTexture BuildHeightTexture(Vector3[,] data, int w, int d)
{
    var img = Image.Create(w, d, false, Image.Format.Rf); // 32-bit float R
    for (int x = 0; x < w; x++)
    for (int z = 0; z < d; z++)
    {
        float h = data[x, z].Y;
        img.SetPixel(x, z, new Color(h, 0, 0, 1));
    }
    return ImageTexture.CreateFromImage(img);
}

private static ImageTexture BuildXYZTexture(Vector3[,] data, int w, int d)
{
    var img = Image.Create(w, d, false, Image.Format.Rgbf); // 3×32-bit float
    for (int x = 0; x < w; x++)
    for (int z = 0; z < d; z++)
    {
        var p = data[x, z];
        img.SetPixel(x, z, new Color(p.X, p.Y, p.Z, 1f));
    }
    return ImageTexture.CreateFromImage(img);
}