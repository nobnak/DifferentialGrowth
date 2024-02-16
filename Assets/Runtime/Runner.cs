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

        protected GraphicsList<Particle> particles;
        protected GLMaterial gl;

        #region unity
        void OnEnable() {
            gl = new GLMaterial();
            particles = new(size => {
                var buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size, Marshal.SizeOf<Particle>());
                return buf;
            });
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
        #endregion

        #region declarations
        public struct Particle {
            public float2 position;
            public float2 velocity;
        }
        #endregion
    }
}