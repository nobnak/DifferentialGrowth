using Gist2.Adapter;
using LLGraphicsUnity;
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
        protected GLMaterial gl;

        #region unity
        void OnEnable() {
            gl = new GLMaterial();
            particles = new(size => {
                var buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size, Marshal.SizeOf<Particle>());
                return buf;
            });

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
            var dist_insert = tuner.baseDist;
            var dist_repulse = dist_insert * tuner.repulsion_dist;

            var dt = 1f / 30; // Time.deltaTime;
            var eps = dist_insert * EPSILON;

            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
                float2 accel = default;
                var n_neighboars = 0;
                for (var j = 0; j < particles.Count; j++) {
                    if (i == j) continue;
                    var q = particles[j];
                    var dx = q.position - p.position;
                    var dist_sq = math.lengthsq(dx);
                    if (eps < dist_sq && dist_sq < dist_repulse * dist_repulse) {
                        var dist = math.sqrt(dist_sq);
                        var f = dx / (dist_sq * dist);
                        accel -= f * 0.1f;
                        n_neighboars++;
                    }
                }

                if (n_neighboars > 0)
                    p.velocity += accel * (dt / n_neighboars);
                particles[i] = p;
            }

            var dumper = math.saturate(tuner.dump_velocity * dt);
            for (var i = 0; i < particles.Count; i++) {
                var p = particles[i];
                p.position += p.velocity * dt;
                p.velocity = math.lerp(p.velocity, 0, dumper);
                particles[i] = p;
            }

            for (var i = 0; i < particles.Count; i++) {
                var p0 = particles[i];
                var p1 = particles[(i + 1) % particles.Count];
                var dist_sq = math.distancesq(p0.position, p1.position);
                if (dist_sq > dist_insert * dist_insert) {
                    var p = new Particle() {
                        position = (p0.position + p1.position) / 2,
                        velocity = p1.position - p0.position,
                    };
                    particles.Insert(i + 1, p);
                    i++;
                }
            }
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
        #endregion

        #region declarations
        public const float EPSILON = 1e-2f;

        public struct Particle {
            public float2 position;
            public float2 velocity;
        }

        [System.Serializable]
        public class Tuner {
            public float baseDist = 1f;
            public float repulsion_dist = 5f;
            public float dump_velocity = 10f;
        }
        #endregion
    }
}