using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MAAYAI.Matrix.Swarm
{
    /// <summary>
    /// Runs the spatial-hash-grid boids pipeline in SwarmCompute.compute every
    /// frame and renders the swarm with Graphics.RenderMeshIndirect straight
    /// from GPU-computed instance matrices. No per-boid GameObjects, no CPU readback.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SwarmManager : MonoBehaviour
    {
        // Must match `struct Boid` in SwarmCompute.compute.
        [StructLayout(LayoutKind.Sequential)]
        private struct BoidData
        {
            public Vector3 position;
            public float swimPhase;
            public Vector3 velocity;
            public float padding;
        }

        private const int BoidStride = sizeof(float) * 8;         // struct Boid
        private const int GridEntryStride = sizeof(uint) * 2;     // struct GridEntry
        private const int RenderDataStride = sizeof(float) * 16;  // struct BoidRenderData

        /// <summary>
        /// Mirrors `cbuffer SwarmParams` in SwarmCompute.compute. Every row is 16 bytes,
        /// so C# sequential layout and HLSL constant-buffer packing agree byte for byte.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SwarmParams
        {
            public uint boidCount;
            public uint paddedCount;
            public uint cellCount;
            public uint maxNeighbours;

            public Vector3 gridOrigin;
            public float invCellSize;

            public Vector3 gridDims;
            public float deltaTime;

            public float perceptionRadiusSq;
            public float separationRadiusSq;
            public float alignmentRadiusSq;
            public float cohesionRadiusSq;

            public float separationWeight;
            public float alignmentWeight;
            public float cohesionWeight;
            public float boundsWeight;

            public float minSpeed;
            public float maxSpeed;
            public float maxSteerForce;
            public float boidScale;

            public Vector3 boundsCenter;
            public float swimSpeed;

            public Vector3 boundsExtents;
            public float pad1;

            public const int Size = 128;
        }

        private const int MaxDispatchGroups = 65535;
        private const int MaxBoids = 1 << 21;       // bitonic padding keeps P within dispatch limits
        private const int MaxGridCells = 1 << 20;   // 8 MB of cell ranges; cell size grows past this
        private const int LocalSortBlock = 512;     // B: compare distances below this run inside one dispatch
        private const int TargetFrameRate = 60;

        /// <summary>A compute kernel with its cached thread group size.</summary>
        private readonly struct Kernel
        {
            public readonly int Id;
            private readonly int _groupSize;

            public Kernel(ComputeShader shader, string name)
            {
                Id = shader.FindKernel(name);
                shader.GetKernelThreadGroupSizes(Id, out uint x, out _, out _);
                _groupSize = (int)x;
            }

            public void Dispatch(ComputeShader shader, int elementCount)
            {
                shader.Dispatch(Id, (elementCount + _groupSize - 1) / _groupSize, 1, 1);
            }

            public int MaxElements => _groupSize * MaxDispatchGroups;
        }

        private static readonly string[] KernelNames =
        {
            "CSClearGrid", "CSHashBoids", "CSBitonicSortGlobal", "CSBitonicSortLocal", "CSBuildGrid", "CSSwarmUpdate"
        };

        // Constant buffer
        private static readonly int SwarmParamsId        = Shader.PropertyToID("SwarmParams");
        // Material properties mirrored into the simulation
        private static readonly int SwimSpeedId          = Shader.PropertyToID("_SwimSpeed");
        // Buffers
        private static readonly int BoidsInId            = Shader.PropertyToID("_BoidsIn");
        private static readonly int BoidsSortedOutId     = Shader.PropertyToID("_BoidsSortedOut");
        private static readonly int BoidsSortedId        = Shader.PropertyToID("_BoidsSorted");
        private static readonly int BoidsOutId           = Shader.PropertyToID("_BoidsOut");
        private static readonly int BoidRenderDataOutId  = Shader.PropertyToID("_BoidRenderDataOut");
        private static readonly int GridEntriesId        = Shader.PropertyToID("_GridEntries");
        private static readonly int CellStartId          = Shader.PropertyToID("_CellStart");
        private static readonly int CellEndId            = Shader.PropertyToID("_CellEnd");
        private static readonly int CellStartReadId      = Shader.PropertyToID("_CellStartRead");
        private static readonly int CellEndReadId        = Shader.PropertyToID("_CellEndRead");
        // Sort step globals
        private static readonly int SortKId              = Shader.PropertyToID("_SortK");
        private static readonly int SortJId              = Shader.PropertyToID("_SortJ");
        private static readonly int BlockSizeId          = Shader.PropertyToID("_BlockSize");
        // Rendering
        private static readonly int BoidRenderDataId     = Shader.PropertyToID("_BoidRenderData");

        [Header("Assets")]
        public ComputeShader swarmCompute;
        [Tooltip("Mesh modelled facing +Z (e.g. a pyramid with its tip on +Z).")]
        public Mesh boidMesh;
        [Tooltip("Material using MAAYAI/Swarm/BoidIndirectLit (reads _BoidRenderData).")]
        public Material boidMaterial;

        [Header("Population")]
        [Min(1)] public int boidCount = 4096;
        [Min(0f)] public float spawnRadius = 10f;
        [Min(0.001f)] public float boidScale = 0.25f;
        public int randomSeed = 1337;

        [Header("Simulation Volume (centered on this transform)")]
        public Vector3 boundsExtents = new Vector3(25f, 15f, 25f);
        [Min(0f)] public float boundsWeight = 4f;

        [Header("Speed")]
        [Min(0f)] public float minSpeed = 2f;
        [Min(0f)] public float maxSpeed = 6f;
        [Min(0f)] public float maxSteerForce = 4f;
        [Tooltip("Clamps simulation step to keep the swarm stable through frame hitches.")]
        [Min(0.001f)] public float maxDeltaTime = 1f / 30f;

        [Header("Rules")]
        [Min(0f)] public float separationRadius = 1f;
        [Min(0f)] public float alignmentRadius = 2.5f;
        [Min(0f)] public float cohesionRadius = 3.5f;
        [Min(0f)] public float separationWeight = 1.5f;
        [Min(0f)] public float alignmentWeight = 1f;
        [Min(0f)] public float cohesionWeight = 1f;
        [Tooltip("Stop scanning after this many in-range neighbours. 0 = unlimited (still capped at 64 by the shader).")]
        [Min(0)] public int maxNeighbours = 48;

        [Header("Rendering")]
        public ShadowCastingMode castShadows = ShadowCastingMode.On;
        public bool receiveShadows = true;
        [Tooltip("Camera that renders the swarm. Empty = Camera.main.")]
        public Camera renderCamera;
        [Tooltip("Also render in every other camera, including the Scene view. Off avoids paying for the swarm twice in the Editor.")]
        public bool renderInAllCameras;

        /// <summary>Current grid resolution (cells per axis).</summary>
        public Vector3Int GridDimensions => _gridDims;
        /// <summary>Current cell edge length. Equals the perception radius unless the cell budget forced it larger.</summary>
        public float CellSize => _cellSize;
        /// <summary>Compute dispatches issued per frame (diagnostics).</summary>
        public int DispatchesPerFrame { get; private set; }
        /// <summary>True once buffers are allocated and the pipeline is running.</summary>
        public bool IsReady => _initialized;

        // Per-instance copy so buffer bindings never collide between swarms.
        private ComputeShader _compute;
        private Kernel _clearGrid, _hashBoids, _sortGlobal, _sortLocal, _buildGrid, _swarmUpdate;

        private GraphicsBuffer _paramsBuffer;       // cbuffer SwarmParams
        private GraphicsBuffer _stateBuffer;        // authoritative boids
        private GraphicsBuffer _sortedBuffer;       // cell-sorted scratch copy
        private GraphicsBuffer _renderDataBuffer;   // per-boid 3x4 matrices, rendered
        private GraphicsBuffer _entryBuffer;        // (cellIndex, boidIndex), P entries
        private GraphicsBuffer _cellStartBuffer;
        private GraphicsBuffer _cellEndBuffer;
        private GraphicsBuffer _commandBuffer;      // indirect draw args

        private readonly SwarmParams[] _params = new SwarmParams[1];
        private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _drawArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
        private MaterialPropertyBlock _propertyBlock;

        private int _allocatedBoidCount;
        private int _paddedCount;
        private int _blockSize;
        private int _allocatedCellCount;

        private Vector3Int _gridDims;
        private Vector3 _gridOrigin;
        private float _cellSize;
        private bool _initialized;

        // Emitters recycle existing boids through a ring cursor; the population never changes.
        private BoidData[] _emitScratch = new BoidData[64];
        private int _emitCursor;
        private System.Random _emitRng = new System.Random(7919);

        private void Start()
        {
            // Step 0: uncapped rendering pegs "GPU usage" regardless of workload.
            Application.targetFrameRate = TargetFrameRate;

            if (!ValidateSetup())
            {
                enabled = false;
                return;
            }

            _compute = Instantiate(swarmCompute);
            _compute.name = swarmCompute.name + " (Instance)";

            _clearGrid   = new Kernel(_compute, KernelNames[0]);
            _hashBoids   = new Kernel(_compute, KernelNames[1]);
            _sortGlobal  = new Kernel(_compute, KernelNames[2]);
            _sortLocal   = new Kernel(_compute, KernelNames[3]);
            _buildGrid   = new Kernel(_compute, KernelNames[4]);
            _swarmUpdate = new Kernel(_compute, KernelNames[5]);

            _propertyBlock = new MaterialPropertyBlock();

            // One constant buffer shared by every kernel; contents uploaded once per frame.
            _paramsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Constant, 1, SwarmParams.Size);
            _compute.SetConstantBuffer(SwarmParamsId, _paramsBuffer, 0, SwarmParams.Size);

            AllocateBoidBuffers();
            UpdateGrid();
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized)
                return;

            // Hot-resize if the population was changed in the Inspector at runtime.
            if (boidCount != _allocatedBoidCount)
                AllocateBoidBuffers();

            UpdateGrid();
            UploadParams(Mathf.Min(Time.deltaTime, maxDeltaTime));
            DispatchPipeline();
            Render();
        }

        private void OnDestroy()
        {
            _initialized = false;
            ReleaseBoidBuffers();
            ReleaseGridBuffers();

            _paramsBuffer?.Release();
            _paramsBuffer = null;

            if (_compute != null)
            {
                Destroy(_compute);
                _compute = null;
            }
        }

        // ---------------------------------------------------------------------
        //  Setup
        // ---------------------------------------------------------------------

        private bool ValidateSetup()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("[SwarmManager] Compute shaders are not supported on this platform.", this);
                return false;
            }

            if (swarmCompute == null || boidMesh == null || boidMaterial == null)
            {
                Debug.LogError("[SwarmManager] Assign swarmCompute, boidMesh and boidMaterial.", this);
                return false;
            }

            foreach (string kernelName in KernelNames)
            {
                if (!swarmCompute.HasKernel(kernelName))
                {
                    Debug.LogError($"[SwarmManager] Kernel '{kernelName}' not found in {swarmCompute.name}.", this);
                    return false;
                }
            }

            int paramsSize = Marshal.SizeOf<SwarmParams>();
            if (paramsSize != SwarmParams.Size)
            {
                Debug.LogError($"[SwarmManager] SwarmParams is {paramsSize} bytes; the cbuffer expects {SwarmParams.Size}.", this);
                return false;
            }

            return true;
        }

        private void AllocateBoidBuffers()
        {
            ReleaseBoidBuffers();

            boidCount = Mathf.Clamp(boidCount, 1, Mathf.Min(MaxBoids, _swarmUpdate.MaxElements));
            _allocatedBoidCount = boidCount;
            _emitCursor = 0;
            _paddedCount = Mathf.NextPowerOfTwo(boidCount);
            _blockSize = Mathf.Min(LocalSortBlock, _paddedCount);

            _stateBuffer      = new GraphicsBuffer(GraphicsBuffer.Target.Structured, boidCount, BoidStride);
            _sortedBuffer     = new GraphicsBuffer(GraphicsBuffer.Target.Structured, boidCount, BoidStride);
            _renderDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, boidCount, RenderDataStride);
            _entryBuffer      = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _paddedCount, GridEntryStride);
            _stateBuffer.SetData(CreateInitialBoids(boidCount));

            _commandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _drawArgs[0] = new GraphicsBuffer.IndirectDrawIndexedArgs
            {
                indexCountPerInstance = boidMesh.GetIndexCount(0),
                instanceCount = (uint)boidCount,
                startIndex = boidMesh.GetIndexStart(0),
                baseVertexIndex = boidMesh.GetBaseVertex(0),
                startInstance = 0   // the render shader relies on SV_InstanceID being 0-based
            };
            _commandBuffer.SetData(_drawArgs);

            // Bindings persist on the per-instance compute shader; set once per allocation.
            _compute.SetInt(BlockSizeId, _blockSize);

            _compute.SetBuffer(_hashBoids.Id, BoidsInId, _stateBuffer);
            _compute.SetBuffer(_hashBoids.Id, GridEntriesId, _entryBuffer);

            _compute.SetBuffer(_sortGlobal.Id, GridEntriesId, _entryBuffer);
            _compute.SetBuffer(_sortLocal.Id, GridEntriesId, _entryBuffer);

            _compute.SetBuffer(_buildGrid.Id, BoidsInId, _stateBuffer);
            _compute.SetBuffer(_buildGrid.Id, BoidsSortedOutId, _sortedBuffer);
            _compute.SetBuffer(_buildGrid.Id, GridEntriesId, _entryBuffer);

            _compute.SetBuffer(_swarmUpdate.Id, BoidsSortedId, _sortedBuffer);
            _compute.SetBuffer(_swarmUpdate.Id, BoidsOutId, _stateBuffer);
            _compute.SetBuffer(_swarmUpdate.Id, BoidRenderDataOutId, _renderDataBuffer);

            _propertyBlock.SetBuffer(BoidRenderDataId, _renderDataBuffer);

            DispatchesPerFrame = CountDispatches();
        }

        private BoidData[] CreateInitialBoids(int count)
        {
            var boids = new BoidData[count];
            var previousState = Random.state;
            Random.InitState(randomSeed);

            Vector3 center = transform.position;
            float initialSpeed = (minSpeed + maxSpeed) * 0.5f;

            for (int i = 0; i < count; i++)
            {
                boids[i].position = center + Random.insideUnitSphere * spawnRadius;
                boids[i].velocity = Random.onUnitSphere * initialSpeed;
                // Random starting stroke phase so the swarm never animates in lockstep.
                boids[i].swimPhase = Random.value;
            }

            Random.state = previousState;
            return boids;
        }

        /// <summary>
        /// Fits a uniform grid (cell size = perception radius) around the simulation
        /// volume, and reallocates the cell buffers only when the cell count changes.
        /// </summary>
        private void UpdateGrid()
        {
            float perceptionRadius = Mathf.Max(0.01f, Mathf.Max(separationRadius, Mathf.Max(alignmentRadius, cohesionRadius)));

            // One-cell margin; positions are hard-clamped to the bounds by the shader.
            Vector3 halfSize = Abs(boundsExtents) + Vector3.one * perceptionRadius;
            Vector3 size = halfSize * 2f;

            // Cell size must stay >= perception radius for the 3x3x3 search to be exact,
            // so the cell budget is enforced by growing cells, never shrinking them.
            float cellSize = perceptionRadius;
            Vector3Int dims = DimsFor(size, cellSize);
            while ((long)dims.x * dims.y * dims.z > MaxGridCells)
            {
                cellSize *= 1.25f;
                dims = DimsFor(size, cellSize);
            }

            _cellSize = cellSize;
            _gridDims = dims;
            _gridOrigin = transform.position - halfSize;

            int cellCount = dims.x * dims.y * dims.z;
            if (cellCount == _allocatedCellCount)
                return;

            ReleaseGridBuffers();
            _allocatedCellCount = cellCount;
            _cellStartBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellCount, sizeof(uint));
            _cellEndBuffer   = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellCount, sizeof(uint));

            _compute.SetBuffer(_clearGrid.Id, CellStartId, _cellStartBuffer);
            _compute.SetBuffer(_clearGrid.Id, CellEndId, _cellEndBuffer);

            _compute.SetBuffer(_buildGrid.Id, CellStartId, _cellStartBuffer);
            _compute.SetBuffer(_buildGrid.Id, CellEndId, _cellEndBuffer);

            _compute.SetBuffer(_swarmUpdate.Id, CellStartReadId, _cellStartBuffer);
            _compute.SetBuffer(_swarmUpdate.Id, CellEndReadId, _cellEndBuffer);
        }

        private static Vector3Int DimsFor(Vector3 size, float cellSize)
        {
            return new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(size.x / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(size.y / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(size.z / cellSize)));
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // ---------------------------------------------------------------------
        //  Per frame
        // ---------------------------------------------------------------------

        /// <summary>Fills SwarmParams and uploads it in a single SetData call.</summary>
        private void UploadParams(float deltaTime)
        {
            float perceptionRadius = Mathf.Max(separationRadius, Mathf.Max(alignmentRadius, cohesionRadius));

            _params[0] = new SwarmParams
            {
                boidCount = (uint)_allocatedBoidCount,
                paddedCount = (uint)_paddedCount,
                cellCount = (uint)_allocatedCellCount,
                maxNeighbours = (uint)Mathf.Max(0, maxNeighbours),

                gridOrigin = _gridOrigin,
                invCellSize = 1f / _cellSize,

                gridDims = new Vector3(_gridDims.x, _gridDims.y, _gridDims.z),
                deltaTime = deltaTime,

                perceptionRadiusSq = perceptionRadius * perceptionRadius,
                separationRadiusSq = separationRadius * separationRadius,
                alignmentRadiusSq = alignmentRadius * alignmentRadius,
                cohesionRadiusSq = cohesionRadius * cohesionRadius,

                separationWeight = separationWeight,
                alignmentWeight = alignmentWeight,
                cohesionWeight = cohesionWeight,
                boundsWeight = boundsWeight,

                minSpeed = Mathf.Min(minSpeed, maxSpeed),
                maxSpeed = maxSpeed,
                maxSteerForce = maxSteerForce,
                boidScale = boidScale,

                boundsCenter = transform.position,
                // The material stays the single source of truth, so Inspector tweaks apply live.
                swimSpeed = boidMaterial.HasProperty(SwimSpeedId) ? boidMaterial.GetFloat(SwimSpeedId) : 0f,
                boundsExtents = boundsExtents
            };

            _paramsBuffer.SetData(_params);
        }

        private void DispatchPipeline()
        {
            ComputeShader cs = _compute;
            int blockCount = _paddedCount / _blockSize;

            // 1. Clear cell ranges.
            _clearGrid.Dispatch(cs, _allocatedCellCount);

            // 2. Hash every boid to a cell.
            _hashBoids.Dispatch(cs, _paddedCount);

            // 3. Compressed bitonic sort.
            //    a) Fully sort every block of B entries in one dispatch.
            cs.SetInt(SortKId, 0);
            _sortLocal.Dispatch(cs, blockCount);

            //    b) Merge upward: global steps for j >= B, then one local dispatch for j < B.
            for (int k = _blockSize << 1; k <= _paddedCount; k <<= 1)
            {
                cs.SetInt(SortKId, k);
                for (int j = k >> 1; j >= _blockSize; j >>= 1)
                {
                    cs.SetInt(SortJId, j);
                    _sortGlobal.Dispatch(cs, _paddedCount);
                }

                _sortLocal.Dispatch(cs, blockCount);
            }

            //    c) Gather boids into sorted order and record [start, end) per cell.
            _buildGrid.Dispatch(cs, _allocatedBoidCount);

            // 4. Boids rules + instance matrices.
            _swarmUpdate.Dispatch(cs, _allocatedBoidCount);
        }

        private int CountDispatches()
        {
            int sortDispatches = 1;
            for (int k = _blockSize << 1; k <= _paddedCount; k <<= 1)
                sortDispatches += Mathf.RoundToInt(Mathf.Log(k / _blockSize, 2)) + 1;

            return 4 + sortDispatches;   // clear + hash + build + update
        }

        private void Render()
        {
            // Positions are hard-clamped to the bounds; pad by the mesh size only.
            Vector3 padding = Vector3.one * (boidScale * 4f);

            var renderParams = new RenderParams(boidMaterial)
            {
                worldBounds = new Bounds(transform.position, Abs(boundsExtents) * 2f + padding),
                matProps = _propertyBlock,
                shadowCastingMode = castShadows,
                receiveShadows = receiveShadows,
                layer = gameObject.layer,
                // null renders in every camera (Scene view included); a camera restricts it.
                camera = renderInAllCameras ? null : (renderCamera != null ? renderCamera : Camera.main)
            };

            Graphics.RenderMeshIndirect(renderParams, boidMesh, _commandBuffer);
        }

        // ---------------------------------------------------------------------
        //  Emission (additive API; the compute pipeline is unchanged)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Re-births <paramref name="count"/> boids at the given world-space points, cycling through
        /// them, by overwriting a ring-buffer slice of the state buffer. The population stays
        /// constant: emitting recycles the oldest-written slots. At most two SetData calls per emit.
        /// </summary>
        /// <param name="points">World-space emission points.</param>
        /// <param name="pointCount">How many entries of <paramref name="points"/> to use.</param>
        /// <param name="velocity">World-space launch velocity shared by the emitted boids.</param>
        /// <param name="count">Boids to emit this call; clamped to the population.</param>
        /// <param name="jitter">Random offset radius around each point, in metres.</param>
        /// <returns>The number of boids actually emitted.</returns>
        public int Emit(Vector3[] points, int pointCount, Vector3 velocity, int count, float jitter)
        {
            if (!_initialized || _stateBuffer == null || points == null)
                return 0;

            pointCount = Mathf.Min(pointCount, points.Length);
            count = Mathf.Min(count, _allocatedBoidCount);
            if (pointCount <= 0 || count <= 0)
                return 0;

            if (_emitScratch.Length < count)
                _emitScratch = new BoidData[Mathf.NextPowerOfTwo(count)];

            var speedJitter = velocity.magnitude * 0.15f;
            for (int i = 0; i < count; i++)
            {
                _emitScratch[i].position = points[i % pointCount] + RandomInSphere() * jitter;
                _emitScratch[i].velocity = velocity + RandomInSphere() * speedJitter;
                _emitScratch[i].swimPhase = (float)_emitRng.NextDouble();
                _emitScratch[i].padding = 0f;
            }

            // Write up to the end of the buffer, then wrap to the start.
            int first = Mathf.Min(count, _allocatedBoidCount - _emitCursor);
            _stateBuffer.SetData(_emitScratch, 0, _emitCursor, first);
            if (count > first)
                _stateBuffer.SetData(_emitScratch, first, 0, count - first);

            _emitCursor = (_emitCursor + count) % _allocatedBoidCount;
            return count;
        }

        private Vector3 RandomInSphere()
        {
            // Rejection sampling: allocation-free and independent of UnityEngine.Random's global state.
            while (true)
            {
                var p = new Vector3(
                    (float)_emitRng.NextDouble() * 2f - 1f,
                    (float)_emitRng.NextDouble() * 2f - 1f,
                    (float)_emitRng.NextDouble() * 2f - 1f);
                if (p.sqrMagnitude <= 1f)
                    return p;
            }
        }

        // ---------------------------------------------------------------------
        //  Cleanup
        // ---------------------------------------------------------------------

        private void ReleaseBoidBuffers()
        {
            _stateBuffer?.Release();
            _stateBuffer = null;
            _sortedBuffer?.Release();
            _sortedBuffer = null;
            _renderDataBuffer?.Release();
            _renderDataBuffer = null;
            _entryBuffer?.Release();
            _entryBuffer = null;
            _commandBuffer?.Release();
            _commandBuffer = null;
            _allocatedBoidCount = 0;
        }

        private void ReleaseGridBuffers()
        {
            _cellStartBuffer?.Release();
            _cellStartBuffer = null;
            _cellEndBuffer?.Release();
            _cellEndBuffer = null;
            _allocatedCellCount = 0;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0.6f, 0.35f);
            Gizmos.DrawWireCube(transform.position, boundsExtents * 2f);

            if (!_initialized)
                return;

            Gizmos.color = new Color(1f, 0.8f, 0f, 0.15f);
            Gizmos.DrawWireCube(_gridOrigin + (Vector3)_gridDims * (_cellSize * 0.5f), (Vector3)_gridDims * _cellSize);
        }
    }
}
