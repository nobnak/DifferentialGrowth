using Gist2.Adapter;
using LLGraphicsUnity;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace DiffentialGrowth {

    public class Runner : MonoBehaviour {
        [SerializeField]
        protected Tuner tuner = new();

        protected GraphicsList<Particle> particles;
        protected List<float3> boundingLines;

        protected GLMaterial gl;

        #region unity
        void OnEnable() {
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
        void Update() {
            var dist_insert = tuner.maxDistance * tuner.scale;
            var dist_repulse = tuner.repulsion_dist * tuner.scale;
            var dist_attract = tuner.minDistance * tuner.scale;

            var f_attract = tuner.attraction_force;
            var f_repulse = tuner.repulsion_force;
            var f_align = tuner.alignment_force;

            var eps = dist_insert * EPSILON;
            var dt = tuner.timeStep * tuner.scale;

            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
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
                for (var j = 0; j < particles.Count; j++) {
                    if (i == j) continue;
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
            var r = 1f;
            for (var i = 0; i < n; i++) {
                var a = i * (2 * math.PI / n);
                var p = new Particle() {
                    position = new float2(math.cos(a) * r, math.sin(a) * r),
                    velocity = new float2(0, 0),
                };
                particles.Add(p);
            }
        }

        private void InitBoundary() {
            var c = Camera.main;
            var z = c.WorldToScreenPoint(new Vector3(0, 0, 0)).z;
            var viewportVertices = new float2[] { new float2(0, 0), new float2(1, 0), new float2(1, 1), new float2(0, 1) };
            boundingLines.Clear();
            var vertices_wc = new List<float3>();
            for (var i = 0; i < viewportVertices.Length; i++) {
                var v = viewportVertices[i];
                float3 p_wc = c.ViewportToWorldPoint(new float3(v.x, v.y, z));
                vertices_wc.Add(p_wc);
            }
            for (var i = 0; i < vertices_wc.Count; i++) {
                var p0 = vertices_wc[i].xy;
                var p1 = vertices_wc[(i + 1) % vertices_wc.Count].xy;
                var diff = p1 - p0;
                var n = math.normalize(new float2(diff.y, -diff.x));
                var d = math.dot(p0, n);
                boundingLines.Add(new float3(n, d));
            }
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