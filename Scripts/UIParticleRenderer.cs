﻿using UnityEngine;
using UnityEngine.UI;
using Coffee.UIParticleExtensions;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace Coffee.UIExtensions
{
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("")]
    internal class UIParticleRenderer : MaskableGraphic
    {
        private static readonly CombineInstance[] s_CombineInstances = new CombineInstance[] { new CombineInstance() };
        private static ParticleSystem.Particle[] s_Particles = new ParticleSystem.Particle[2048];
        private static readonly List<Material> s_Materials = new List<Material>(2);
        private static MaterialPropertyBlock s_Mpb;

        private ParticleSystemRenderer _renderer;
        private ParticleSystem _particleSystem;
        //private ParticleSystem _emitter;
        private UIParticle _parent;
        private int _index;
        private bool _isTrail;
        private Material _modifiedMaterial;
        private Vector3 _prevScale;
        private Vector3 _prevPsPos;
        private Vector2Int _prevScreenSize;
        private bool _delay = false;
        private bool _prewarm = false;

        public override Texture mainTexture
        {
            get
            {
                return _isTrail ? null : _particleSystem.GetTextureForSprite();
            }
        }

        public override bool raycastTarget
        {
            get
            {
                return false;
            }
        }

        public static UIParticleRenderer AddRenderer(UIParticle parent, int index)
        {
            // Create renderer object.
            var go = new GameObject("UIParticleRenderer", typeof(UIParticleRenderer))
            {
                hideFlags = HideFlags.DontSave,
                layer = parent.gameObject.layer,
            };

            // Set parent.
            var transform = go.transform;
            transform.SetParent(parent.transform, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            // Add renderer component.
            var renderer = go.GetComponent<UIParticleRenderer>();
            renderer._parent = parent;
            renderer._index = index;

            return renderer;
        }

        /// <summary>
        /// Perform material modification in this function.
        /// </summary>
        public override Material GetModifiedMaterial(Material baseMaterial)
        {
            if (!IsActive()) return baseMaterial;

            var modifiedMaterial = base.GetModifiedMaterial(baseMaterial);

            // 
            var texture = mainTexture;
            if (texture == null && _parent.m_AnimatableProperties.Length == 0)
            {
                ModifiedMaterial.Remove(_modifiedMaterial);
                _modifiedMaterial = null;
                return modifiedMaterial;
            }

            //
            var id = _parent.m_AnimatableProperties.Length == 0 ? 0 : GetInstanceID();
            modifiedMaterial = ModifiedMaterial.Add(modifiedMaterial, texture, id);
            ModifiedMaterial.Remove(_modifiedMaterial);
            _modifiedMaterial = modifiedMaterial;

            return modifiedMaterial;
        }

        public void Clear(int index = -1)
        {
            if (_renderer)
            {
                _renderer.enabled = true;
            }
            _parent = null;
            _particleSystem = null;
            _renderer = null;
            if (0 <= index )
            {
                _index = index;
            }
            //_emitter = null;

            material = null;
            enabled = false;

            workerMesh.Clear();
            canvasRenderer.SetMesh(workerMesh);
        }

        public void Set(UIParticle parent, ParticleSystem particleSystem, bool isTrail)
        {
            _parent = parent;
            maskable = parent.maskable;

            gameObject.layer = parent.gameObject.layer;

            _particleSystem = particleSystem;
            if (_particleSystem.isPlaying)
            {
                _particleSystem.Clear();
            }
            _prewarm = _particleSystem.main.prewarm;

            _renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
            _renderer.enabled = false;

            //_emitter = emitter;
            _isTrail = isTrail;

            _renderer.GetSharedMaterials(s_Materials);
            material = s_Materials[isTrail ? 1 : 0];
            s_Materials.Clear();

            // Support sprite.
            var tsa = particleSystem.textureSheetAnimation;
            if (tsa.mode == ParticleSystemAnimationMode.Sprites && tsa.uvChannelMask == 0)
                tsa.uvChannelMask = UVChannelFlags.UV0;

            _prevScale = GetWorldScale();
            _prevPsPos = _particleSystem.transform.position;
            _prevScreenSize = new Vector2Int(Screen.width, Screen.height);
            _delay = true;

            canvasRenderer.SetTexture(null);

            enabled = true;
        }

        public void UpdateMesh(Camera bakeCamera)
        {
            // No particle to render: Clear mesh.
            if (
                !enabled || !_particleSystem || !_parent || !canvasRenderer || !canvas || !bakeCamera
                || !transform.lossyScale.GetScaled(_parent.scale3D).IsVisible()     // Scale is not visible.
                || (!_particleSystem.IsAlive() && !_particleSystem.isPlaying)       // No particle.
                || (_isTrail && !_particleSystem.trails.enabled)                    // Trail, but it is not enabled.
#if UNITY_2018_3_OR_NEWER
                || canvasRenderer.GetInheritedAlpha() < 0.01f                       // #102: Do not bake particle system to mesh when the alpha is zero.
#endif
        )
            {
                Profiler.BeginSample("[UIParticleRenderer] Clear Mesh");
                workerMesh.Clear();
                canvasRenderer.SetMesh(workerMesh);
                Profiler.EndSample();

                return;
            }

            var main = _particleSystem.main;
            var scale = GetWorldScale();
            var psPos = _particleSystem.transform.position;

            // Simulate particles.
            if (!_isTrail)
            {
                Profiler.BeginSample("[UIParticle] Bake Mesh > Simulate Particles");
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    SimulateForEditor(psPos - _prevPsPos, scale);
                }
                else
#endif
                {
                    ResolveResolutionChange(psPos, scale);
                    Simulate(scale, _parent.isPaused || _delay);

                    if (_delay && !_parent.isPaused)
                    {
                        Simulate(scale, _parent.isPaused);
                    }

                    // When the ParticleSystem simulation is complete, stop it.
                    if (!main.loop && main.duration <= _particleSystem.time && (_particleSystem.IsAlive() || _particleSystem.particleCount == 0))
                    {
                        _particleSystem.Stop(false);
                    }
                }
                Profiler.EndSample();
                _prevScale = scale;
                _prevPsPos = psPos;
                _delay = false;
            }

            // Bake mesh.
            Profiler.BeginSample("[UIParticleRenderer] Bake Mesh");
            {
                if (_isTrail)
                {
                    _renderer.BakeTrailsMesh(s_CombineInstances[0].mesh, bakeCamera, true);
                }
                else if (_renderer.CanBakeMesh())
                {
                    _renderer.BakeMesh(s_CombineInstances[0].mesh, bakeCamera, true);
                }
                else
                {
                    s_CombineInstances[0].mesh.Clear();
                }
            }
            Profiler.EndSample();

            // Combine mesh to transform. ([ParticleSystem local ->] world -> renderer local)
            Profiler.BeginSample("[UIParticleRenderer] Combine Mesh");
            {
                s_CombineInstances[0].transform = canvasRenderer.transform.worldToLocalMatrix * GetWorldMatrix(psPos, scale);
                workerMesh.CombineMeshes(s_CombineInstances, true, true);
            }
            Profiler.EndSample();

            // Set mesh to the CanvasRenderer.
            Profiler.BeginSample("[UIParticleRenderer] Set Mesh");
            canvasRenderer.SetMesh(workerMesh);
            Profiler.EndSample();

            // Update animatable material properties.
            Profiler.BeginSample("[UIParticleRenderer] Update Animatable Material Properties");
            UpdateMaterialProperties();
            Profiler.EndSample();
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            if (!s_CombineInstances[0].mesh)
            {
                s_CombineInstances[0].mesh = new Mesh()
                {
                    name = "[UIParticleRenderer] Combine Instance Mesh",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();

            ModifiedMaterial.Remove(_modifiedMaterial);
            _modifiedMaterial = null;
        }

        /// <summary>
        /// Call to update the geometry of the Graphic onto the CanvasRenderer.
        /// </summary>
        protected override void UpdateGeometry()
        {
        }

        private Vector3 GetWorldScale()
        {
            Profiler.BeginSample("[UIParticleRenderer] GetWorldScale");
            var scale = _parent.scale3D;
            //if (_parent.uiScaling)
            {
                scale.Scale(_parent.transform.localScale.Inverse());
            }
            //else if (_parent.scalingMode == UIParticle.ScalingMode.UI && _particleSystem.main.scalingMode != ParticleSystemScalingMode.Hierarchy)
            //{
            //    var gscale = _parent.transform.lossyScale.GetScaled(canvas.transform.lossyScale.Inverse());
            //    scale.Scale(gscale * canvas.scaleFactor);
            //}
            Profiler.EndSample();
            return scale;
        }

        private Matrix4x4 GetWorldMatrix(Vector3 psPos, Vector3 scale)
        {
            var space = _particleSystem.GetActualSimulationSpace();
            if (_isTrail && _particleSystem.trails.worldSpace)
            {
                space = ParticleSystemSimulationSpace.World;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                switch (space)
                {
                    case ParticleSystemSimulationSpace.World:
                        return Matrix4x4.Translate(psPos)
                            * Matrix4x4.Scale(scale)
                            * Matrix4x4.Translate(-psPos);
                }
            }
#endif

            switch (space)
            {
                case ParticleSystemSimulationSpace.Local:
                    return Matrix4x4.Translate(psPos)
                        * Matrix4x4.Scale(scale);
                case ParticleSystemSimulationSpace.World:
                    return Matrix4x4.Scale(scale);
                case ParticleSystemSimulationSpace.Custom:
                    return Matrix4x4.Translate(_particleSystem.main.customSimulationSpace.position.GetScaled(scale))
                        //* Matrix4x4.Translate(wpos)
                        * Matrix4x4.Scale(scale)
                        //* Matrix4x4.Translate(-wpos)
                        ;
                default:
                    throw new System.NotSupportedException();
            }
        }

        /// <summary>
        /// For world simulation, interpolate particle positions when the screen size is changed.
        /// </summary>
        /// <param name="psPos"></param>
        /// <param name="scale"></param>
        private void ResolveResolutionChange(Vector3 psPos, Vector3 scale)
        {
            var screenSize = new Vector2Int(Screen.width, Screen.height);
            //if ((_prevScreenSize != screenSize || _prevScale != scale) && _particleSystem.main.simulationSpace == ParticleSystemSimulationSpace.World && _parent.uiScaling)
            if ((_prevScreenSize != screenSize || _prevScale != scale) && _particleSystem.main.simulationSpace == ParticleSystemSimulationSpace.World)
            {
                // Update particle array size and get particles.
                var size = _particleSystem.particleCount;
                if (s_Particles.Length < size)
                {
                    s_Particles = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(size)];
                }
                _particleSystem.GetParticles(s_Particles, size);

                // Resolusion resolver:
                // (psPos / scale) / (prevPsPos / prevScale) -> psPos * scale.inv * prevPsPos.inv * prevScale
                var modifier = psPos.GetScaled(scale.Inverse(), _prevPsPos.Inverse(), _prevScale);
                for (var i = 0; i < size; i++)
                {
                    var particle = s_Particles[i];
                    particle.position = particle.position.GetScaled(modifier);
                    s_Particles[i] = particle;
                }
                _particleSystem.SetParticles(s_Particles, size);

                // Delay: Do not progress in the frame where the resolution has been changed.
                _delay = true;
                _prevScale = scale;
                _prevPsPos = psPos;
            }
            _prevScreenSize = screenSize;
        }

        private void Simulate(Vector3 scale, bool paused)
        {
            var main = _particleSystem.main;
            var deltaTime = paused
                ? 0
                : main.useUnscaledTime
                    ? Time.unscaledDeltaTime
                    : Time.deltaTime;

            // Prewarm: 
            if (0 < deltaTime && _prewarm)
            {
                deltaTime += main.duration;
                _prewarm = false;
            }

            // Normal simulation for non-scaling or local spacing.
            var isScaling = scale != Vector3.one;
            if (!isScaling || _particleSystem.GetActualSimulationSpace() == ParticleSystemSimulationSpace.Local)
            {
                _particleSystem.Simulate(deltaTime, false, false, false);
                return;
            }

            // get world position.
            var psTransform = _particleSystem.transform;
            var originWorldPosition = psTransform.position;
            var originWorldRotation = psTransform.rotation;

            var emission = _particleSystem.emission;
            var rateOverDistance = emission.enabled && 0 < emission.rateOverDistance.constant && 0 < emission.rateOverDistanceMultiplier;
            if (rateOverDistance)
            {
                // (For rate-over-distance emission,) Move to previous scaled position, simulate (delta = 0).
                Vector3 prevScaledPos = _prevPsPos.GetScaled(_prevScale.Inverse());
                psTransform.SetPositionAndRotation(prevScaledPos, originWorldRotation);
                _particleSystem.Simulate(0, false, false, false);
            }

            // Move to scaled position, simulate, revert to origin position.
            var scaledPos = originWorldPosition.GetScaled(scale.Inverse());
            psTransform.SetPositionAndRotation(scaledPos, originWorldRotation);
            _particleSystem.Simulate(deltaTime, false, false, false);
            psTransform.SetPositionAndRotation(originWorldPosition, originWorldRotation);
        }

#if UNITY_EDITOR
        private void SimulateForEditor(Vector3 diffPos, Vector3 scale)
        {
            // Extra world simulation.
            if (_particleSystem.main.simulationSpace == ParticleSystemSimulationSpace.World && 0 < Vector3.SqrMagnitude(diffPos))
            {
                Profiler.BeginSample("[UIParticle] Bake Mesh > Extra world simulation");
                diffPos.x *= 1f - 1f / Mathf.Max(0.001f, scale.x);
                diffPos.y *= 1f - 1f / Mathf.Max(0.001f, scale.y);
                diffPos.z *= 1f - 1f / Mathf.Max(0.001f, scale.z);

                var count = _particleSystem.particleCount;
                if (s_Particles.Length < count)
                {
                    var size = Mathf.NextPowerOfTwo(count);
                    s_Particles = new ParticleSystem.Particle[size];
                }

                _particleSystem.GetParticles(s_Particles);
                for (var j = 0; j < count; j++)
                {
                    var p = s_Particles[j];
                    p.position += diffPos;
                    s_Particles[j] = p;
                }

                _particleSystem.SetParticles(s_Particles, count);
                Profiler.EndSample();
            }
        }
#endif

        private void UpdateMaterialProperties()
        {
            if (_parent.m_AnimatableProperties.Length == 0) return;

            if (s_Mpb == null)
                s_Mpb = new MaterialPropertyBlock();

            _renderer.GetPropertyBlock(s_Mpb);
            if (s_Mpb.isEmpty) return;

            // #41: Copy the value from MaterialPropertyBlock to CanvasRenderer
            if (!_modifiedMaterial) return;

            foreach (var ap in _parent.m_AnimatableProperties)
            {
                ap.UpdateMaterialProperties(_modifiedMaterial, s_Mpb);
            }

            s_Mpb.Clear();
        }
    }
}