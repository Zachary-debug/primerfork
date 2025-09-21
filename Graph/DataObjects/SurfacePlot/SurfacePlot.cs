using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using PrimerTools.TweenSystem;
using PrimerTools.Utilities;

namespace PrimerTools.Graph;

[Tool]
public partial class SurfacePlot : MeshInstance3D, IPrimerGraphData
{
    #region Data space transform
    public delegate Vector3 Transformation(Vector3 inputPoint);
    public Transformation TransformPointFromDataSpaceToPositionSpace = point => point;
    #endregion

    #region Appearance
    private StandardMaterial3D _materialCache;
    private StandardMaterial3D Material
    {
        get => _materialCache ??= new StandardMaterial3D();
        set => _materialCache = value;
    }
    
    public void SetColor(Color color)
    {
        if (color.A < 1)
        {
            Material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }
        
        Material.AlbedoColor = color;
    }
    #endregion
    
    #region Data
    private List<Vector3[,]> _surfaceStates = new List<Vector3[,]>();
    private float _stateProgress = 0f;
    
    public enum SweepMode { XAxis, ZAxis, Radial, Diagonal }
    // private SweepMode _currentSweepMode = SweepMode.XAxis;
    
    [Export] public float StateProgress
    {
        get => _stateProgress;
        set
        {
            _stateProgress = value;
            UpdateMeshForCurrentProgress();
        }
    }
    
    public delegate Vector3[,] DataFetch();
    public DataFetch DataFetchMethod = () =>
    {
        PrimerGD.PrintWithStackTrace("Data fetch method not assigned. Returning empty array.");
        return new Vector3[0, 0];
    };

    public void FetchData()
    {
        var data = DataFetchMethod();
        AddState(data);
    }
    
    public void SetData(Vector3[,] heightData)
    {
        AddState(heightData);
    }
    
    public void AddState(Vector3[,] stateData)
    {
        _surfaceStates.Add(stateData);
    }

    // TODO: Make the min/max/points arguments optional.
    public void SetDataWithHeightFunction(Func<float, float, float> heightFunction,
        float minX, float maxX, int xPoints,
        float minZ, float maxZ, int zPoints)
    {
        AddStateWithHeightFunction(heightFunction, minX, maxX, xPoints, minZ, maxZ, zPoints);
    }
    
    public void AddStateWithHeightFunction(Func<float, float, float> heightFunction,
        float minX, float maxX, int xPoints,
        float minZ, float maxZ, int zPoints)
    {
        var data = new Vector3[xPoints, zPoints];

        var xStep = (maxX - minX) / (xPoints - 1);
        var zStep = (maxZ - minZ) / (zPoints - 1);

        for (var i = 0; i < xPoints; i++)
        {
            var x = minX + i * xStep;
            for (var j = 0; j < zPoints; j++)
            {
                var z = minZ + j * zStep;
                data[i, j] = new Vector3(x, heightFunction(x, z), z);
            }
        }
        
        AddState(data);
    }

    
    #endregion

    #region Mesh Management
    private ArrayMesh _reusableMesh;
    private Rid _meshRid;
    private bool _meshInitialized = false;
    private int _currentWidth;
    private int _currentDepth;
    private byte[] _vertexDataBuffer;
    private byte[] _normalDataBuffer;
    private Vector3[] _normalVectorBuffer;
    #endregion

    // Implement IPrimerGraphData interface methods
    public Animation Transition(double duration = AnimationUtilities.DefaultDuration)
    {
        // For now, just create the mesh and return a default animation
        CreateMesh();
        return new Animation();
    }

    public IStateChange TransitionStateChange(double duration = Node3DStateChangeExtensions.DefaultDuration)
    {
        // Default to TransitionAppear for backward compatibility
        return TransitionAppear(duration);
    }
    
    public IStateChange TransitionAppear(double duration = Node3DStateChangeExtensions.DefaultDuration)
    {
        return TransitionToNextState(duration);
    }
    
    public IStateChange TransitionToNextState(double duration = Node3DStateChangeExtensions.DefaultDuration)
    {
        if (_surfaceStates.Count == 0) return null;
        
        GD.Print($"{Name} Transitioning from state {StateProgress} to state {_surfaceStates.Count}");
        
        // Don't go beyond the last state
        // if (targetProgress > _surfaceStates.Count - 1)
        //     targetProgress = _surfaceStates.Count - 1;
            
        return new PropertyStateChange(this, "StateProgress", _surfaceStates.Count)
            .WithDuration(duration);
    }
    
    public IStateChange TransitionToState(int stateIndex, double duration = Node3DStateChangeExtensions.DefaultDuration)
    {
        if (stateIndex < 0 || stateIndex >= _surfaceStates.Count) return null;
        
        return new PropertyStateChange(this, "StateProgress", (float)stateIndex)
            .WithDuration(duration);
    }
    
    private void UpdateMeshForCurrentProgress()
    {
        if (_surfaceStates.Count == 0)
        {
            Mesh = new ArrayMesh();
            return;
        }

        // Handle initial appearance (0 to 1 progress)
        if (_stateProgress < 1f)
        {
            CreateSweptMesh(_stateProgress);
            return;
        }

        // For progress >= 1, we're showing states or transitioning between them
        // Progress 1.0 = state 0, Progress 2.0 = state 1, etc.
        var adjustedProgress = _stateProgress - 1f;
        var stateIndex = Mathf.FloorToInt(adjustedProgress);
        var transitionProgress = adjustedProgress - stateIndex;

        // Clamp to valid state range
        if (stateIndex >= _surfaceStates.Count - 1)
        {
            // We're at or beyond the last state
            UpdateMeshVertices(_surfaceStates[_surfaceStates.Count - 1]);
            return;
        }

        // If we're exactly on a state (no fractional part), show it
        if (Mathf.IsEqualApprox(transitionProgress, 0f))
        {
            UpdateMeshVertices(_surfaceStates[stateIndex]);
            return;
        }

        // Interpolate between two states
        var fromState = _surfaceStates[stateIndex];
        var toState = _surfaceStates[stateIndex + 1];
        CreateInterpolatedMesh(fromState, toState, transitionProgress);
    }
    
    private void EnsureMeshInitialized(int width, int depth)
    {
        if (_meshInitialized && _currentWidth == width && _currentDepth == depth)
            return;
        
        // Create the mesh structure once with fixed topology
        CreateInitialMesh(width, depth);
        _meshInitialized = true;
        _currentWidth = width;
        _currentDepth = depth;
    }
    
    private void CreateInitialMesh(int width, int depth)
    {
        _reusableMesh ??= new ArrayMesh();
        
        // Clear any existing surfaces
        for (int i = _reusableMesh.GetSurfaceCount() - 1; i >= 0; i--)
            _reusableMesh.ClearSurfaces();
        
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        
        // Create initial vertices (will be updated later)
        var vertices = new Vector3[width * depth];
        if (_surfaceStates.Count > 0)
        {
            var initialData = _surfaceStates[0];
            var index = 0;
            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < depth; z++)
                {
                    vertices[index++] = TransformPointFromDataSpaceToPositionSpace(initialData[x, z]);
                }
            }
        }
        
        var indices = new List<int>();
        
        // Create triangles (topology stays constant)
        for (var x = 0; x < width - 1; x++)
        {
            for (var z = 0; z < depth - 1; z++)
            {
                var topLeft = x * depth + z;
                var topRight = topLeft + 1;
                var bottomLeft = (x + 1) * depth + z;
                var bottomRight = bottomLeft + 1;
                
                indices.Add(topLeft);
                indices.Add(bottomLeft);
                indices.Add(bottomRight);
                
                indices.Add(topLeft);
                indices.Add(bottomRight);
                indices.Add(topRight);
            }
        }
        
        // Initialize with dummy normals (will be updated)
        var normals = new Vector3[width * depth];
        for (int i = 0; i < normals.Length; i++)
            normals[i] = Vector3.Up;
        
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        
        Material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        
        _reusableMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Mesh = _reusableMesh;
        Mesh.SurfaceSetMaterial(0, Material);
        
        // Cache the RID for direct updates
        _meshRid = _reusableMesh.GetRid();
        
        // Pre-allocate buffers
        _vertexDataBuffer = new byte[width * depth * 12]; // 3 floats * 4 bytes each
        _normalDataBuffer = new byte[width * depth * 12]; // 3 floats * 4 bytes each
        _normalVectorBuffer = new Vector3[width * depth]; // Reusable normal vector array
    }
    
    private void UpdateMeshVertices(Vector3[,] data)
    {
        var width = data.GetLength(0);
        var depth = data.GetLength(1);
        
        EnsureMeshInitialized(width, depth);
        
        // Pack vertices into byte array
        var index = 0;
        for (var x = 0; x < width; x++)
        {
            for (var z = 0; z < depth; z++)
            {
                var vertex = TransformPointFromDataSpaceToPositionSpace(data[x, z]);
                BitConverter.GetBytes(vertex.X).CopyTo(_vertexDataBuffer, index);
                BitConverter.GetBytes(vertex.Y).CopyTo(_vertexDataBuffer, index + 4);
                BitConverter.GetBytes(vertex.Z).CopyTo(_vertexDataBuffer, index + 8);
                index += 12;
            }
        }
        
        // Update vertex positions
        RenderingServer.MeshSurfaceUpdateVertexRegion(_meshRid, 0, 0, _vertexDataBuffer);
        
        // Calculate and update normals
        UpdateNormals(data);
    }
    
    private void UpdateNormals(Vector3[,] data)
    {
        var width = data.GetLength(0);
        var depth = data.GetLength(1);
        
        // Ensure normal buffer is the right size
        if (_normalVectorBuffer == null || _normalVectorBuffer.Length != width * depth)
        {
            _normalVectorBuffer = new Vector3[width * depth];
        }
        
        // Calculate normals based on the grid structure
        for (var x = 0; x < width; x++)
        {
            for (var z = 0; z < depth; z++)
            {
                var idx = x * depth + z;
                var current = TransformPointFromDataSpaceToPositionSpace(data[x, z]);
                
                // Calculate normal using neighboring points
                Vector3 normal;
                
                if (x > 0 && x < width - 1 && z > 0 && z < depth - 1)
                {
                    // Interior point - use cross product of differences
                    var left = TransformPointFromDataSpaceToPositionSpace(data[x - 1, z]);
                    var right = TransformPointFromDataSpaceToPositionSpace(data[x + 1, z]);
                    var front = TransformPointFromDataSpaceToPositionSpace(data[x, z - 1]);
                    var back = TransformPointFromDataSpaceToPositionSpace(data[x, z + 1]);
                    
                    var dx = right - left;
                    var dz = back - front;
                    normal = dz.Cross(dx).Normalized();
                }
                else
                {
                    // Edge point - use simplified calculation
                    normal = Vector3.Up;
                }
                
                _normalVectorBuffer[idx] = normal;
            }
        }
        
        // Pack normals into byte array
        var index = 0;
        for (var i = 0; i < _normalVectorBuffer.Length; i++)
        {
            BitConverter.GetBytes(_normalVectorBuffer[i].X).CopyTo(_normalDataBuffer, index);
            BitConverter.GetBytes(_normalVectorBuffer[i].Y).CopyTo(_normalDataBuffer, index + 4);
            BitConverter.GetBytes(_normalVectorBuffer[i].Z).CopyTo(_normalDataBuffer, index + 8);
            index += 12;
        }
        
        // Update normals (attribute index 1 for normals)
        RenderingServer.MeshSurfaceUpdateAttributeRegion(_meshRid, 0, 0, _normalDataBuffer);
    }
    
    // TODO: Just use a shader for this
    // TODO: Honestly, this whole class should use a vertext shader.
    private void CreateSweptMesh(float progress)
    {
        if (_surfaceStates.Count == 0 || progress <= 0)
        {
            Mesh = new ArrayMesh();
            return;
        }
        
        var data = _surfaceStates[0];
        var width = data.GetLength(0);
        var depth = data.GetLength(1);
        
        // Calculate visible width based on progress
        var visibleWidth = (int)(width * progress);
        var edgeFraction = (width * progress) % 1;

        // We need at least 2 columns to create triangles
        if (visibleWidth < 2)
        {
            if (visibleWidth == 1 || edgeFraction > 0)
            {
                // Show a very thin slice using first two columns
                visibleWidth = 2;
                edgeFraction = 0;
                // Scale the second column to be very close to the first
                var scaleFactor = (width * progress) / 2.0f;
                
                // Create vertices with interpolation
                var vertices = new List<Vector3>();
                for (var x = 0; x < 2; x++)
                {
                    for (var z = 0; z < depth; z++)
                    {
                        if (x == 0)
                        {
                            vertices.Add(TransformPointFromDataSpaceToPositionSpace(data[0, z]));
                        }
                        else
                        {
                            // Interpolate between first and second column based on progress
                            var point = data[0, z].Lerp(data[1, z], scaleFactor);
                            vertices.Add(TransformPointFromDataSpaceToPositionSpace(point));
                        }
                    }
                }
                
                // Now create triangles with these 2 columns
                var indices = new List<int>();
                for (var z = 0; z < depth - 1; z++)
                {
                    var topLeft = z;
                    var topRight = topLeft + 1;
                    var bottomLeft = depth + z;
                    var bottomRight = bottomLeft + 1;
                    
                    indices.Add(topLeft);
                    indices.Add(bottomLeft);
                    indices.Add(bottomRight);
                    
                    indices.Add(topLeft);
                    indices.Add(bottomRight);
                    indices.Add(topRight);
                }
                
                BuildMeshFromVerticesAndIndices(vertices, indices);
                return;
            }
            else
            {
                // Progress is too small to show anything
                Mesh = new ArrayMesh();
                return;
            }
        }
        
        // Rest of the original method for visibleWidth >= 2
        var vertices2 = new List<Vector3>();
        var indices2 = new List<int>();
        
        // Create vertices up to visible width
        for (var x = 0; x < visibleWidth; x++)
        {
            for (var z = 0; z < depth; z++)
            {
                vertices2.Add(TransformPointFromDataSpaceToPositionSpace(data[x, z]));
            }
        }
        
        // Add interpolated edge vertices if needed
        if (edgeFraction > 0 && visibleWidth < width)
        {
            for (var z = 0; z < depth; z++)
            {
                var currentPoint = data[visibleWidth - 1, z];
                var nextPoint = data[visibleWidth, z];
                var interpolatedPoint = currentPoint.Lerp(nextPoint, edgeFraction);
                vertices2.Add(TransformPointFromDataSpaceToPositionSpace(interpolatedPoint));
            }
        }
        
        // Create triangles
        var actualWidth = edgeFraction > 0 ? visibleWidth + 1 : visibleWidth;
        for (var x = 0; x < actualWidth - 1; x++)
        {
            for (var z = 0; z < depth - 1; z++)
            {
                var topLeft = x * depth + z;
                var topRight = topLeft + 1;
                var bottomLeft = (x + 1) * depth + z;
                var bottomRight = bottomLeft + 1;
                
                indices2.Add(topLeft);
                indices2.Add(bottomLeft);
                indices2.Add(bottomRight);
                
                indices2.Add(topLeft);
                indices2.Add(bottomRight);
                indices2.Add(topRight);
            }
        }
        
        BuildMeshFromVerticesAndIndices(vertices2, indices2);
    }
    
    private void CreateInterpolatedMesh(Vector3[,] fromData, Vector3[,] toData, float progress)
    {
        var width = Math.Min(fromData.GetLength(0), toData.GetLength(0));
        var depth = Math.Min(fromData.GetLength(1), toData.GetLength(1));
        
        EnsureMeshInitialized(width, depth);
        
        // Interpolate vertices directly into the buffer
        var index = 0;
        for (var x = 0; x < width; x++)
        {
            for (var z = 0; z < depth; z++)
            {
                var fromPoint = fromData[x, z];
                var toPoint = toData[x, z];
                var interpolatedPoint = fromPoint.Lerp(toPoint, progress);
                var vertex = TransformPointFromDataSpaceToPositionSpace(interpolatedPoint);
                
                BitConverter.GetBytes(vertex.X).CopyTo(_vertexDataBuffer, index);
                BitConverter.GetBytes(vertex.Y).CopyTo(_vertexDataBuffer, index + 4);
                BitConverter.GetBytes(vertex.Z).CopyTo(_vertexDataBuffer, index + 8);
                index += 12;
            }
        }
        
        // Update vertex positions
        RenderingServer.MeshSurfaceUpdateVertexRegion(_meshRid, 0, 0, _vertexDataBuffer);
        
        // For interpolated meshes, we could interpolate normals too, but recalculating is more accurate
        // Create a temporary interpolated data array for normal calculation
        var interpolatedData = new Vector3[width, depth];
        for (var x = 0; x < width; x++)
        {
            for (var z = 0; z < depth; z++)
            {
                interpolatedData[x, z] = fromData[x, z].Lerp(toData[x, z], progress);
            }
        }
        UpdateNormals(interpolatedData);
    }

    public Tween TweenTransition(double duration = AnimationUtilities.DefaultDuration)
    {
        // For now, just create the mesh and return a default tween
        CreateMesh();
        return CreateTween();
    }

    public IStateChange Disappear()
    {
        throw new NotImplementedException();
    }

    // public Animation Disappear()
    // {
    //     // For now, return an empty animation
    //     return new Animation();
    // }

    private void CreateMesh()
    {
        if (_surfaceStates.Count == 0)
        {
            GD.Print("No height data available to create mesh.");
            return;
        }
        
        UpdateMeshVertices(_surfaceStates[_surfaceStates.Count - 1]);
    }
    
    private void BuildMeshFromVerticesAndIndices(List<Vector3> vertices, List<int> indices)
    {
        // Initialize once
        _reusableMesh ??= new ArrayMesh();
        
        // Clear existing surfaces
        for (int i = _reusableMesh.GetSurfaceCount() - 1; i >= 0; i--)
            _reusableMesh.ClearSurfaces();
        
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        
        var normals = CalculateNormals(vertices, indices);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        
        Material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        
        _reusableMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        Mesh = _reusableMesh;
        Mesh.SurfaceSetMaterial(0, Material);
    }
    
    private Vector3[] CalculateNormals(List<Vector3> vertices, List<int> indices)
    {
        var normals = new Vector3[vertices.Count];

        // Initialize normals array
        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = Vector3.Zero;
        }

        // Calculate normals for each triangle and add to the vertices
        for (var i = 0; i < indices.Count; i += 3)
        {
            var index1 = indices[i];
            var index2 = indices[i + 1];
            var index3 = indices[i + 2];

            var side1 = vertices[index2] - vertices[index1];
            var side2 = vertices[index3] - vertices[index1];
            var normal = side2.Cross(side1).Normalized();

            // Add the normal to each vertex of the triangle
            normals[index1] += normal;
            normals[index2] += normal;
            normals[index3] += normal;
        }

        // Normalize all normals
        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].Normalized();
        }

        return normals;
    }
}

// Initial attempt at a shader-based approach below

// using Godot;
// using System;
// using System.Collections.Generic;
// using PrimerTools.TweenSystem;
// using PrimerTools.Utilities;
//
// namespace PrimerTools.Graph;
//
// [Tool]
// public partial class SurfacePlot : MeshInstance3D, IPrimerGraphData
// {
//     #region Data space transform (kept – mapped to uniforms)
//     public delegate Vector3 Transformation(Vector3 inputPoint);
//     public Transformation TransformPointFromDataSpaceToPositionSpace = p => p;
//     #endregion
//
//     #region Appearance
//     private StandardMaterial3D _stdMaterialCache; // used only for Albedo color passthrough
//     private ShaderMaterial _shaderMat;
//     private Shader _shaderHeight;
//     private Shader _shaderParametric;
//
//     private Color _albedoColor = Colors.White;
//
//     public void SetColor(Color color)
//     {
//         _albedoColor = color;
//         if (_shaderMat != null)
//         {
//             _shaderMat.SetShaderParameter("albedo_color", _albedoColor);
//             _shaderMat.SetShaderParameter("use_vertex_lighting", false);
//         }
//     }
//     #endregion
//
//     #region States & inputs
//     // Case 1 / 2: height states (R float)
//     private readonly List<ImageTexture> _heightStates = new();
//
//     // Optional Case 3: parametric XYZ (RGB float)
//     private readonly List<ImageTexture> _xyzStates = new();
//
//     private float _stateProgress = 0f;
//
//     [Export]
//     public float StateProgress
//     {
//         get => _stateProgress;
//         set
//         {
//             _stateProgress = value;
//             ApplyProgress();
//         }
//     }
//
//     public delegate Vector3[,] DataFetch();
//     public DataFetch DataFetchMethod = () =>
//     {
//         PrimerGD.PrintWithStackTrace("Data fetch method not assigned. Returning empty array.");
//         return new Vector3[0, 0];
//     };
//
//     public void FetchData() => AddState(DataFetchMethod());
//
//     public void SetData(Vector3[,] heightData) => AddState(heightData);
//
//     public void AddState(Vector3[,] stateData)
//     {
//         EnsureGridFromData(stateData, out var minX, out var maxX, out var minZ, out var maxZ, out var width, out var depth);
//         var tex = BuildHeightTexture(stateData, width, depth);
//         _heightStates.Add(tex);
//
//         _gridWidth = width;
//         _gridDepth = depth;
//         _minX = minX; _maxX = maxX; _minZ = minZ; _maxZ = maxZ;
//         EnsureStaticGridMesh(); // one-time plane/grid with UVs
//         EnsureHeightShader();
//         PushSpaceUniforms();
//     }
//
//     // Direct math → generate data grid, treat as Case 1
//     public void SetDataWithHeightFunction(Func<float, float, float> f,
//         float minX, float maxX, int xPoints,
//         float minZ, float maxZ, int zPoints)
//     {
//         AddStateWithHeightFunction(f, minX, maxX, xPoints, minZ, maxZ, zPoints);
//     }
//
//     public void AddStateWithHeightFunction(Func<float, float, float> f,
//         float minX, float maxX, int xPoints,
//         float minZ, float maxZ, int zPoints)
//     {
//         var data = new Vector3[xPoints, zPoints];
//         var dx = (maxX - minX) / (xPoints - 1);
//         var dz = (maxZ - minZ) / (zPoints - 1);
//         for (int i = 0; i < xPoints; i++)
//         {
//             float x = minX + i * dx;
//             for (int j = 0; j < zPoints; j++)
//             {
//                 float z = minZ + j * dz;
//                 data[i, j] = new Vector3(x, f(x, z), z);
//             }
//         }
//         AddState(data);
//     }
//     #endregion
//
//     #region Mesh & shader setup (static grid; shader displaces)
//     private ArrayMesh _gridMesh;
//     private int _gridWidth, _gridDepth;
//     private float _minX, _maxX, _minZ, _maxZ;
//
//     // uniforms derived from TransformPointFromDataSpaceToPositionSpace (linearized)
//     private Vector3 _offset = Vector3.Zero;
//     private Vector3 _scale = Vector3.One;
//
//     public Animation Transition(double duration = AnimationUtilities.DefaultDuration)
//         => new Animation(); // no-op (visuals handled by shader)
//
//     public IStateChange TransitionStateChange(double duration = Node3DStateChangeExtensions.DefaultDuration)
//         => TransitionAppear(duration);
//
//     public IStateChange TransitionAppear(double duration = Node3DStateChangeExtensions.DefaultDuration)
//         => TransitionToNextState(duration);
//
//     public IStateChange TransitionToNextState(double duration = Node3DStateChangeExtensions.DefaultDuration)
//     {
//         if (_heightStates.Count == 0 && _xyzStates.Count == 0) return null;
//         var target = _heightStates.Count; // same meaning as before
//         return new PropertyStateChange(this, "StateProgress", (float)target).WithDuration(duration);
//     }
//
//     public IStateChange TransitionToState(int stateIndex, double duration = Node3DStateChangeExtensions.DefaultDuration)
//     {
//         if (stateIndex < 0 || stateIndex >= Math.Max(_heightStates.Count, _xyzStates.Count)) return null;
//         return new PropertyStateChange(this, "StateProgress", 1f + stateIndex).WithDuration(duration);
//     }
//
//     public Tween TweenTransition(double duration = AnimationUtilities.DefaultDuration)
//         => CreateTween(); // external drives StateProgress; shader does the rest
//
//     public IStateChange Disappear() => throw new NotImplementedException();
//
//     private void EnsureStaticGridMesh()
//     {
//         if (_gridMesh != null) { Mesh = _gridMesh; return; }
//
//         // Build a unit-UV grid (row-major vertices) sized by _gridWidth/_gridDepth.
//         // VERTEX.xz will be computed in shader from UV + uniform ranges.
//         _gridMesh = new ArrayMesh();
//         var arrays = new Godot.Collections.Array();
//         arrays.Resize((int)Mesh.ArrayType.Max);
//
//         int w = Math.Max(2, _gridWidth);
//         int d = Math.Max(2, _gridDepth);
//
//         var vertices = new Vector3[w * d];
//         var uvs = new Vector2[w * d];
//         var indices = new int[(w - 1) * (d - 1) * 6];
//
//         int vi = 0;
//         for (int z = 0; z < d; z++)
//         for (int x = 0; x < w; x++)
//         {
//             float u = (float)x / (w - 1);
//             float v = (float)z / (d - 1);
//             vertices[vi] = new Vector3(u, 0f, v);
//             uvs[vi] = new Vector2(u, v);
//             vi++;
//         }
//
//         int ii = 0;
//         for (int z = 0; z < d - 1; z++)
//         for (int x = 0; x < w - 1; x++)
//         {
//             int a = z * w + x;
//             int b = a + 1;
//             int c = (z + 1) * w + x;
//             int d2 = c + 1;
//             indices[ii++] = a; indices[ii++] = c; indices[ii++] = b;
//             indices[ii++] = b; indices[ii++] = c; indices[ii++] = d2;
//         }
//
//         arrays[(int)Mesh.ArrayType.Vertex] = vertices;
//         arrays[(int)Mesh.ArrayType.TexUV] = uvs;
//         arrays[(int)Mesh.ArrayType.Index] = indices;
//
//         _gridMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
//         Mesh = _gridMesh;
//         Mesh.SurfaceSetMaterial(0, GetOrCreateShaderMat(heightMode: true));
//     }
//
//     private ShaderMaterial GetOrCreateShaderMat(bool heightMode)
//     {
//         if (_shaderMat != null) return _shaderMat;
//
//         if (_shaderHeight == null)
//             _shaderHeight = GD.Load<Shader>("res://addons/PrimerTools/Graph/DataObjects/SurfacePlot/SurfacePlot_Height.gdshader");
//         if (_shaderParametric == null)
//             _shaderParametric = GD.Load<Shader>("res://addons/PrimerTools/Graph/DataObjects/SurfacePlot/SurfacePlot_Parametric.gdshader");
//
//         _shaderMat = new ShaderMaterial
//         {
//             Shader = heightMode ? _shaderHeight : _shaderParametric
//         };
//
//         _shaderMat.SetShaderParameter("albedo_color", _albedoColor);
//         _shaderMat.SetShaderParameter("cutoff", 0.0f);
//         _shaderMat.SetShaderParameter("mix_factor", 0.0f);
//         _shaderMat.SetShaderParameter("use_vertex_lighting", false);
//         _shaderMat.SetShaderParameter("cull_disabled", true);
//
//         return _shaderMat;
//     }
//
//     private void EnsureHeightShader()
//     {
//         if (_shaderMat == null)
//             _ = GetOrCreateShaderMat(heightMode: true);
//         else
//             _shaderMat.Shader = _shaderHeight;
//         if (Mesh != null) Mesh.SurfaceSetMaterial(0, _shaderMat);
//     }
//
//     private void EnsureParametricShader()
//     {
//         if (_shaderMat == null)
//             _ = GetOrCreateShaderMat(heightMode: false);
//         else
//             _shaderMat.Shader = _shaderParametric;
//         if (Mesh != null) Mesh.SurfaceSetMaterial(0, _shaderMat);
//     }
//     #endregion
//
//     #region Packing & uniforms
//     private static ImageTexture BuildHeightTexture(Vector3[,] data, int w, int d)
//     {
//         var img = Image.Create(w, d, false, Image.Format.Rf); // 32-bit float R
//         for (int x = 0; x < w; x++)
//         for (int z = 0; z < d; z++)
//         {
//             float h = data[x, z].Y;
//             img.SetPixel(x, z, new Color(h, 0, 0, 1));
//         }
//         return ImageTexture.CreateFromImage(img);
//     }
//
//     private static ImageTexture BuildXYZTexture(Vector3[,] data, int w, int d)
//     {
//         var img = Image.Create(w, d, false, Image.Format.Rgbf); // 3×32-bit float
//         for (int x = 0; x < w; x++)
//         for (int z = 0; z < d; z++)
//         {
//             var p = data[x, z];
//             img.SetPixel(x, z, new Color(p.X, p.Y, p.Z, 1f));
//         }
//         return ImageTexture.CreateFromImage(img);
//     }
//
//     private static void EnsureGridFromData(Vector3[,] data, out float minX, out float maxX, out float minZ, out float maxZ, out int w, out int d)
//     {
//         w = data.GetLength(0);
//         d = data.GetLength(1);
//         minX = data[0, 0].X;
//         maxX = data[w - 1, 0].X;
//         minZ = data[0, 0].Z;
//         maxZ = data[0, d - 1].Z;
//     }
//
//     private void PushSpaceUniforms()
//     {
//         // Try to approximate your transform delegate with a linear map: pos' = pos * scale + offset.
//         // If you need non-linear transforms, encode them in the shader instead.
//         // We’ll derive scale/offset by probing 0/1 corners in data space.
//         var dsMin = new Vector3(_minX, 0, _minZ);
//         var dsMax = new Vector3(_maxX, 0, _maxZ);
//         var psMin = TransformPointFromDataSpaceToPositionSpace(dsMin);
//         var psMax = TransformPointFromDataSpaceToPositionSpace(dsMax);
//
//         // Assume linear in X,Z independently:
//         var dsSize = new Vector3(Mathf.Max(1e-6f, _maxX - _minX), 1, Mathf.Max(1e-6f, _maxZ - _minZ));
//         var psSize = psMax - psMin;
//         _scale = new Vector3(psSize.X / dsSize.X, 1f, psSize.Z / dsSize.Z);
//         _offset = new Vector3(psMin.X - _scale.X * _minX, 0f, psMin.Z - _scale.Z * _minZ);
//
//         _shaderMat?.SetShaderParameter("x_range", new Vector2(_minX, _maxX));
//         _shaderMat?.SetShaderParameter("z_range", new Vector2(_minZ, _maxZ));
//         _shaderMat?.SetShaderParameter("pos_scale", _scale);
//         _shaderMat?.SetShaderParameter("pos_offset", _offset);
//     }
//     #endregion
//
//     #region Progress → shader params
//     private void ApplyProgress()
//     {
//         if (_shaderMat == null) return;
//
//         // Compute indices for A/B
//         ImageTexture aH=null, bH=null, aX=null, bX=null;
//         float cutoff = 1f, mix = 0f;
//
//         if (_stateProgress < 1f) {
//             cutoff = Mathf.Clamp(_stateProgress, 0f, 1f);
//             mix = 0f;
//             if (_heightStates.Count > 0) { aH = bH = _heightStates[0]; EnsureHeightShader(); }
//             else if (_xyzStates.Count > 0) { aX = bX = _xyzStates[0]; EnsureParametricShader(); }
//         } else {
//             float adj = _stateProgress - 1f;
//             int idx = Mathf.FloorToInt(adj);
//             float t = adj - idx;
//             int last = Math.Max(_heightStates.Count, _xyzStates.Count) - 1;
//             if (idx >= last) { idx = last; t = 0f; }
//             cutoff = 1f; mix = t;
//
//             if (_heightStates.Count > 0) {
//                 EnsureHeightShader();
//                 aH = _heightStates[Mathf.Clamp(idx, 0, _heightStates.Count - 1)];
//                 bH = _heightStates[Mathf.Clamp(idx + 1, 0, _heightStates.Count - 1)];
//             } else if (_xyzStates.Count > 0) {
//                 EnsureParametricShader();
//                 aX = _xyzStates[Mathf.Clamp(idx, 0, _xyzStates.Count - 1)];
//                 bX = _xyzStates[Mathf.Clamp(idx + 1, 0, _xyzStates.Count - 1)];
//             }
//         }
//
//         if (aH != null) {
//             _shaderMat.SetShaderParameter("height_tex_a", aH);
//             _shaderMat.SetShaderParameter("height_tex_b", bH);
//         }
//         if (aX != null) {
//             _shaderMat.SetShaderParameter("xyz_tex_a", aX);
//             _shaderMat.SetShaderParameter("xyz_tex_b", bX);
//         }
//
//         _shaderMat.SetShaderParameter("cutoff", cutoff);
//         _shaderMat.SetShaderParameter("mix_factor", mix);
//     }
//     #endregion
//
//     #region Optional parametric XYZ state add (Case 3)
//     public void AddParametricState(Vector3[,] xyzData)
//     {
//         EnsureGridFromData(xyzData, out var minX, out var maxX, out var minZ, out var maxZ, out var w, out var d);
//         var tex = BuildXYZTexture(xyzData, w, d);
//         _xyzStates.Add(tex);
//
//         _gridWidth = w;
//         _gridDepth = d;
//         _minX = minX; _maxX = maxX; _minZ = minZ; _maxZ = maxZ;
//         EnsureStaticGridMesh();
//         EnsureParametricShader();
//         PushSpaceUniforms();
//     }
//     #endregion
// }

