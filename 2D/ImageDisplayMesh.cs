using Godot;
using PrimerTools;

[Tool]
public partial class ImageDisplayMesh : MeshInstance3D
{
    private Texture2D imageTexture;
    private Vector2 size;
    public Vector2 Size => size;
    [Export] public Texture2D ImageTexture
    {
        get => imageTexture;
        set
        {
            if (value == null) return;
            imageTexture = value;
            size = new Vector2(value.GetWidth(), value.GetHeight());
            Update();
        }
    }

    public static ImageDisplayMesh Create(string path)
    {
        var idm = new ImageDisplayMesh();
        idm.ImageTexture = ResourceLoader.Load<Texture2D>(path);
        return idm;
    }

    private void Update()
    {
        if (ImageTexture == null)
        {
            GD.PrintErr("ImageTexture is not assigned.");
            return;
        }

        // Create a plane mesh with the same aspect ratio as the image
        var planeMesh = new PlaneMesh();
        planeMesh.Size = size;

        // Assign the mesh to the MeshInstance3D
        Mesh = planeMesh;

        // Create a new material and set the image texture
        var material = new StandardMaterial3D();
        material.AlbedoTexture = ImageTexture;
        // material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;

        // Assign the material to the mesh surface
        SetSurfaceOverrideMaterial(0, material);
    }

    public void AllowTransparency()
    {
        var mat = this.GetOrCreateOverrideMaterial();
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
    }

    public void MakeUnshaded()
    {
        var mat = this.GetOrCreateOverrideMaterial();
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
    }
}