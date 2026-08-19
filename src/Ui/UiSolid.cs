using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// A plain white block, made once and shared. Two things need one : the strike across a blocked
// icon, and the backing behind a line of text that has to stay readable over whatever the board
// happens to be showing under it.
//
// Written a pixel at a time rather than from an array on purpose. Handing a managed array to a
// texture across the interop boundary is the shape that silently loses its contents here, and a
// four-pixel loop is not worth being clever about.
internal static class UiSolid
{
    private static Sprite _sprite;

    public static Sprite Sprite
    {
        get
        {
            if (_sprite != null) return _sprite;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "TargetingSolid",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                    texture.SetPixel(x, y, Color.white);
            texture.Apply(false, false);
            _sprite = Sprite.Create(texture, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), 100f);
            return _sprite;
        }
    }
}
