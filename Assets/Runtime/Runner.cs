using EffSpace.Extensions;
using EffSpace.Models;
using Gist2.Adapter;
using LLGraphicsUnity;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using static UnityEditor.PlayerSettings;

namespace DiffentialGrowth {

    public class Runner : MonoBehaviour {
        [SerializeField]
        protected Tuner tuner = new();

        protected GraphicsList<Particle> particles;
        protected List<float3> boundingLines;
        protected List<int> elementIds;
        protected FPointGrid grid;

        protected int2 screenSize;
        protected float4x4 screenToWorld;
        protected float2 queryRange;

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

            var verticalCellCount = 1 << 6;
            FPointGridExt.RecommendGrid(screenSize, verticalCellCount, out var cellCount, out var cellSize);
            grid = new(cellCount, cellSize, float2.zero);
            elementIds = new();
            queryRange = cellSize * 2f;

            gl = new GLMaterial();
            particles = new(size => {
                var buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size, Marshal.SizeOf<Particle>());
                return buf;
            });
            boundingLines = new();

            InitBoundary();

            InitParticles();
        }

        void OnDisable() {
            gl?.Dispose();
            gl = null;

            particles?.Dispose();
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
                            using (new GLPrimitiveScope(GL.LINE_STRIP)) {
                                var n = particles.Count;
                                for (int i = 0; i <= n; i++) {
                                    var p = particles[i % n];
                                    GL.Vertex(new float3(p.position, 0f));
                                }
                            }
                        }
                    }
                }
            }
        }
        void Update() {
            var scale = tuner.scale * screenSize.y;
            var dist_insert = tuner.maxDistance * scale;
            var dist_repulse = tuner.repulsion_dist * scale;
            var dist_attract = tuner.minDistance * scale;

            var f_attract = tuner.attraction_force;
            var f_repulse = tuner.repulsion_force;
            var f_align = tuner.alignment_force;

            var eps = dist_insert * EPSILON;
            var dt = tuner.timeStep * tuner.scale;

            grid.Clear();
            elementIds.Clear();
            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
                var element_id = grid.Insert(i, p.position);
                elementIds.Add(element_id);
            }

            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
                var elementId = elementIds[i];
                var i0 = (i + particles.Count - 1) % particles.Count;
                var i1 = (i + 1) % particles.Count;
                float2 velocity = default;

                // attraction
                if (i0 >= 0) {
                    var p0 = particles[i0];
                    var dx = p0.position - p.position;
                    var dist_sq = math.lengthsq(dx);
                    if (dist_sq > dist_attract * dist_attract) {
                        velocity += f_attract * dx;
                    }
                }
                if (i1 >= 0) {
                    var p1 = particles[i1];
                    var dx = p1.position - p.position;
                    var dist_sq = math.lengthsq(dx);
                    if (dist_sq > dist_attract * dist_attract) {
                        velocity += f_attract * dx;
                    }
                }

                // repulsion
                var weights_repulsion = 0f;
                float2 velocity_repulsion = default;
                if (elementId < 0) Debug.LogError($"Element not found at: i={i}");
                foreach (var eid0 in grid.Query(p.position - queryRange, p.position + queryRange)) {
                    if (elementId == eid0) continue;
                    var e = grid.grid.elements[eid0];
                    var j = e.id;
                    var q = particles[j];
                    var dx = q.position - p.position;
                    var dist_sq = math.lengthsq(dx);
                    if (eps < dist_sq && dist_sq < dist_repulse * dist_repulse) {
                        var w = 1f / dist_sq;
                        velocity_repulsion += -(f_repulse * w) * dx;
                        weights_repulsion += w;
                    }
                }
                if (weights_repulsion > 0)
                    velocity += velocity_repulsion / weights_repulsion;

                // alignment
                if (i0 >= 0 && i1 >= 0) {
                    var p0 = particles[i0];
                    var p1 = particles[i1];
                    var p_mid = (p0.position + p1.position) / 2f;
                    var dx = p_mid - p.position;
                    velocity += f_align * dx;
                }

                p.velocity = velocity;
                particles[i] = p;
            }

            // update positions
            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
                p.position += p.velocity * dt;
                particles[i] = p;
            }

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

            // remove overlapping particles
            for (var i = 0; i < particles.Count; ) {
                var i1 = (i + 1) % particles.Count;
                var p0 = particles[i];
                var p1 = particles[i1];
                var dx = p1.position - p0.position;
                var dist_sq = math.lengthsq(dx);
                if (dist_sq < dist_attract * dist_attract) {
                    particles.RemoveAt(i);
                    continue;
                }
                i++;
            }

            // insert new particles
            for (var i = 0; i < particles.Count; i++) {
                var p0 = particles[i];
                var p1 = particles[(i + 1) % particles.Count];
                var dist_sq = math.distancesq(p0.position, p1.position);
                if (dist_sq > dist_insert * dist_insert) {
                    var p = new Particle() {
                        position = (p0.position + p1.position) / 2f,
                        velocity = p1.position - p0.position,
                    };
                    particles.Insert(i + 1, p);
                    i++;
                }
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
            particles.Clear();
            var n = 10;
            var r = screenSize.y >> 2;
            var center = new float2(screenSize.x / 2f, screenSize.y / 2f);
            for (var i = 0; i < n; i++) {
                var a = i * (2 * math.PI / n);
                var pos = new float2(math.cos(a) * r, math.sin(a) * r) + center;
                var p = new Particle() {
                    position = pos,
                    velocity = new float2(0, 0),
                };

                particles.Add(p);
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
        #endregion

        #region declarations
        public const float EPSILON = 1e-2f;

        public struct Particle {
            public float2 position;
            public float2 velocity;
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
        }
        #endregion
    }
}