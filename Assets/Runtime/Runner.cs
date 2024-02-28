using EffSpace.Extensions;
using EffSpace.Models;
using Gist2.Adapter;
using LLGraphicsUnity;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace DiffentialGrowth {

    public class Runner : MonoBehaviour {
        [SerializeField]
        protected Tuner tuner = new();

        protected List<Particle> particles;
        protected List<Wing<int>> indices;
        protected Queue<int> unused;
        protected Queue<Particle> addedParticles;
        protected Queue<Wing<int>> addedIndices;

        protected List<float3> boundingLines;
        protected List<int> elementIds;
        protected FPointGrid grid;

        protected int2 screenSize;
        protected float4x4 screenToWorld;

        protected GLMaterial gl;

        #region unity
        void OnEnable() {
            var c = Camera.main;
            screenSize = new int2(c.pixelWidth, c.pixelHeight);
            var halfHeight = c.orthographicSize;
            var aspect = c.aspect;
            screenToWorld = float4x4.TRS(
                new float3(-aspect * halfHeight, -halfHeight, 0f),
                quaternion.identity,
                new float3(2f * aspect * halfHeight / screenSize.x, 2f * halfHeight / screenSize.y, 1f));

            var verticalCellCount = 1 << math.clamp(tuner.levelOfGrid, 2, 10);
            var boundaryGap = 10;
            FPointGridExt.RecommendGrid(screenSize + boundaryGap * 2, verticalCellCount, out var cellCount, out var cellSize);
            Debug.Log($"Grid: screen={screenSize},cells={cellCount},size={cellSize}");
            grid = new(cellCount, cellSize, boundaryGap);
            elementIds = new();

            gl = new GLMaterial();
            particles = new();
            indices = new();
            unused = new();
            addedParticles = new();
            addedIndices = new();
            boundingLines = new();

            InitBoundary();

            InitParticles();
        }

        void OnDisable() {
            gl?.Dispose();
            gl = null;

            particles = null;
        }
        void OnRenderObject() {
            var c = Camera.current;
            if (gl == null || !isActiveAndEnabled) return;

            var prop = new GLProperty() {
                Color = Color.green,
                ZWriteMode = true,
                ZTestMode = CompareFunction.LessEqual,
            };

            if (c == Camera.main) {
                using (new GLMatrixScope()) {
                    GL.LoadIdentity();
                    GL.modelview = c.worldToCameraMatrix;
                    GL.LoadProjectionMatrix(c.projectionMatrix);

                    using (new GLModelViewScope(screenToWorld)) {
                        using (gl.GetScope(prop)) {
                            using (new GLPrimitiveScope(GL.LINES)) {
                                var n = particles.Count;
                                for (int i = 0; i < n; i++) {
                                    var iw = indices[i];
                                    if (iw.value < 0 || iw.next < 0) continue;

                                    var iw1 = indices[iw.next];
                                    var p0 = particles[iw.value];
                                    var p1 = particles[iw1.value];
                                    GL.Vertex(new float3(p0.position, 0f));
                                    GL.Vertex(new float3(p1.position, 0f));
                                }
                            }
                        }
                    }
                }
            }
        }
        void Update() {
            try {
                BuildGrid();
                UpdateVelocity();
                UpdatePosition();
                RetainBoundary();
                Refine();
            } catch (System.Exception e) {
                PrintIndices();
                throw e;
            }
        }
        #endregion

        #region interface
        public object CuurTuner => tuner;
        public void Invalidate() {}
        public void Restart() {
            InitParticles();
        }
        #endregion

        #region methods
        private void InitParticles() {
            var n = 10;
            var g = 2;
            var r = screenSize.y >> 2;
            var center = new float2(screenSize.x / 2f, screenSize.y / 2f);

            particles.Clear();
            indices.Clear();

            var lines = new List<List<Wing<int>>>();
            List<Wing<int>> line = default;
            for (var i = 0; i < n; i++) {
                if (i % g == 0) {
                    line = new();
                    lines.Add(line);
                }
                var a = i * (2 * math.PI / n);
                var pos = new float2(math.cos(a) * r, math.sin(a) * r) + center;
                var p = new Particle() {
                    position = pos,
                    velocity = new float2(0, 0),
                };

                var node = new Wing<int>() { value = i };
                line.Add(node);
                particles.Add(p);
            }

            foreach (var l in lines) {
                var lineLength = l.Count;
                for (var i = 0; i < lineLength; i++) {
                    var m = l[i];
                    var j = m.value;
                    var i0 = (i > 0) ? j -1 : -1;
                    var i1 = (i < lineLength - 1) ? j + 1 : -1;
                    m.prev = i0;
                    m.next = i1;
                    l[i] = m;
                }
                indices.AddRange(l);
            }
        }

        private void InitBoundary() {
            boundingLines.Clear();

            var vertices = new List<float2> {
                new float2(0f, 0f),
                new float2(0f, screenSize.y),
                screenSize,
                new float2(screenSize.x, 0f)
            };

            for (var i = 0; i < vertices.Count; i++) {
                var p0 = vertices[i];
                var p1 = vertices[(i + 1) % vertices.Count];
                var diff = p1 - p0;
                var n = math.normalize(new float2(-diff.y, diff.x));
                var d = math.dot(p0, n);
                boundingLines.Add(new float3(n, d));
            }
#if UNITY_EDITOR
            var tmp = new StringBuilder($"Boundary\n");
            for (var i = 0; i < boundingLines.Count; i++) {
                var b = boundingLines[i];
                tmp.AppendLine($" {i}:v={b.xy},d={b.z}");
            }
            Debug.Log(tmp);
#endif
        }
        void UpdateVelocity() {
            var scale = tuner.scale * screenSize.y;
            var dist_insert = tuner.maxDistance * scale;
            var dist_repulse = tuner.repulsion_dist * scale;
            var dist_attract = tuner.minDistance * scale;

            var f_attract = tuner.attraction_force;
            var f_repulse = tuner.repulsion_force;
            var f_align = tuner.alignment_force;

            var eps = dist_insert * EPSILON;

            var queryRange = new float2(dist_repulse, dist_repulse);
            var queryCells = math.ceil(queryRange / grid.cellSize);
            if (math.any(queryCells > 10)) {
                Debug.LogWarning($"Querying many cells: n={queryCells}");
            }

            for (var i = 0; i < particles.Count; i++) {
                var iw = indices[i];
                if (iw.value < 0) continue;

                var p = particles[iw.value];
                var elementId = elementIds[iw.value];
                float2 velocity = default;

                // attraction
                if (iw.prev >= 0) {
                    var iw0 = indices[iw.prev];
                    var p0 = particles[iw0.value];
                    var dx = p0.position - p.position;
                    var dist_sq = math.lengthsq(dx);
                    if (dist_sq > dist_attract * dist_attract) {
                        velocity += f_attract * dx;
                    }
                }
                if (iw.next >= 0) {
                    var iw1 = indices[iw.next];
                    var p1 = particles[iw1.value];
                    var dx = p1.position - p.position;
                    var dist_sq = math.lengthsq(dx);
                    if (dist_sq > dist_attract * dist_attract) {
                        velocity += f_attract * dx;
                    }
                }

                // repulsion
                var weights_repulsion = 0f;
                float2 velocity_repulsion = default;
                if (elementId >= 0) {
                    foreach (var eid0 in grid.Query(p.position - queryRange, p.position + queryRange)) {
                        if (elementId == eid0) continue;
                        var e = grid.grid.elements[eid0];
                        var j = e.id;
                        var jw = indices[j];
                        var q = particles[jw.value];
                        var dx = q.position - p.position;
                        var dist_sq = math.lengthsq(dx);
                        if (eps < dist_sq && dist_sq < dist_repulse * dist_repulse) {
                            var w = 1f / dist_sq;
                            velocity_repulsion += -(f_repulse * w) * dx;
                            weights_repulsion += w;
                        }
                    }
                }
                if (weights_repulsion > 0)
                    velocity += velocity_repulsion / weights_repulsion;

                // alignment
                if (iw.prev >= 0 && iw.next >= 0) {
                    var p0 = particles[iw.prev];
                    var p1 = particles[iw.next];
                    var p_mid = (p0.position + p1.position) / 2f;
                    var dx = p_mid - p.position;
                    velocity += f_align * dx;
                }

                p.velocity = velocity;
                particles[i] = p;
            }
        }
        void UpdatePosition() {
            var scale = tuner.scale * screenSize.y;
            var dist_repulse = tuner.repulsion_dist * scale;

            var queryRange = new float2(dist_repulse, dist_repulse);
            var queryCells = math.ceil(queryRange / grid.cellSize);
            if (math.any(queryCells > 10)) {
                Debug.LogWarning($"Querying many cells: n={queryCells}");
            }

            // update positions
            var dt = tuner.timeStep * tuner.scale;
            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
                p.position += p.velocity * dt;
                particles[i] = p;
            }
        }

        void RetainBoundary() {
            // boundary
            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
                for (var j = 0; j < boundingLines.Count; j++) {
                    var b = boundingLines[j];
                    var n = b.xy;
                    var diff = math.dot(p.position, n.xy) - b.z;
                    if (diff > 0f) {
                        p.position -= diff * n;
                    }
                }
                particles[i] = p;
            }
        }

        void Refine() {
            var scale = tuner.scale * screenSize.y;
            var dist_insert = tuner.maxDistance * scale;
            var dist_attract = tuner.minDistance * scale;

            // remove overlapping particles
            for (var i = 0; i < particles.Count; i++) {
                var iw = indices[i];
                if (iw.value < 0) continue;

                if (iw.next >= 0 && iw.prev >= 0) {
                    var iw0 = indices[iw.prev];
                    var iw1 = indices[iw.next];
                    var p0 = particles[iw0.value];
                    var p1 = particles[iw1.value];
                    var dx = p1.position - p0.position;
                    var dist_sq = math.lengthsq(dx);
                    if (dist_sq < 4f * dist_attract * dist_attract) {
                        iw0.next = iw.next;
                        iw.value = -1;
                        iw1.prev = iw.prev;
                        indices[iw.prev] = iw0;
                        indices[i] = iw;
                        indices[iw.next] = iw1;
                        unused.Enqueue(i);
                    }
                }
            }

            // insert new particles
            addedParticles.Clear();
            addedIndices.Clear();
            for (var i = 0; i < particles.Count; i++) {
                var iw0 = indices[i];
                if (iw0.value < 0) continue;

                if (iw0.next >= 0) {
                    var iw1 = indices[iw0.next];
                    var p0 = particles[iw0.value];
                    var p1 = particles[iw1.value];
                    var dist_sq = math.distancesq(p0.position, p1.position);
                    if (dist_sq > dist_insert * dist_insert) {
                        var p = new Particle() {
                            position = (p0.position + p1.position) / 2f
                        };
                        var iw = new Wing<int>() {
                            prev = iw1.prev,
                            next = iw0.next,
                        };
                        addedParticles.Enqueue(p);
                        addedIndices.Enqueue(iw);
                    }
                }
            }
            while (addedParticles.Count > 0) {
                var p = addedParticles.Dequeue();
                var iw = addedIndices.Dequeue();
                if (!unused.TryDequeue(out var j)) {
                    j = particles.Count;
                    particles.Add(default);
                    indices.Add(default);
                }
                iw.value = j;
                particles[j] = p;
                indices[j] = iw;
                if (iw.prev >= 0) {
                    var iw0 = indices[iw.prev];
                    iw0.next = j;
                    indices[iw.prev] = iw0;
                }
                if (iw.next >= 0) {
                    var iw1 = indices[iw.next];
                    iw1.prev = j;
                    indices[iw.next] = iw1;
                }
            }
        }

        private void BuildGrid() {
            grid.Clear();
            elementIds.Clear();
            for (var i = 0; i < particles.Count; i++) {
                var iw = indices[i];
                var element_id = -1;
                if (iw.value >= 0) {
                    var p = particles[iw.value];
                    element_id = grid.Insert(i, p.position);
                }
                elementIds.Add(element_id);
            }
        }

        private void PrintIndices() {
            var tmp = new StringBuilder($"Indices\n");
            for (var i = 0; i < indices.Count; i++) {
                var iw = indices[i];
                tmp.AppendLine($" [{i}]:{iw.value} <{iw.prev},{iw.next}> ");
            }
            Debug.Log(tmp.ToString());
        }
        #endregion

        #region declarations
        public const float EPSILON = 1e-2f;

        public struct Particle {
            public float2 position;
            public float2 velocity;
        }
        public struct Wing<T> {
            public T value;
            public int next;
            public int prev;
        }

        [System.Serializable]
        public class Tuner {
            public float scale = 1f;
            public float timeStep = 0.01f;

            public float minDistance = 1f;
            public float maxDistance = 5f;
            public float repulsion_dist = 10f;

            public float repulsion_force = 0.2f;
            public float attraction_force = 0.5f;
            public float alignment_force = 0.45f;

            [Range(2, 10)]
            public int levelOfGrid = 5;
        }
        #endregion
    }
}