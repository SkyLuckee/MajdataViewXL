#nullable enable

using MajdataViewX.Base;
using MajdataViewX.Notes;
using MajdataViewX.Types.Input;
using MajdataViewX.Types.Rendering;
using MajdataViewX.Utils;
using MajdataViewX.Utils.Extensions;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using static MajdataViewX.Base.MajBurst;
using static MajdataViewX.Base.MajCtx;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace MajdataViewX.Managers
{
    public class InputManager
    {
        // 默认手尺寸
        public const float DEFAULT_HAND_RADIUS = 0.45f;

        // mutable, depends on fps(djauto changes apply in next frame)
        private struct DJAutoAutoplayStartSecKey { }
        public static readonly SharedStatic<float> DJAUTO_AUTOPLAY_START_SEC_SS = SharedStatic<float>.GetOrCreate<InputManager, DJAutoAutoplayStartSecKey>();
        public static float AUTOPLAY_START_SEC => DJAUTO_AUTOPLAY_START_SEC_SS.Data;
        public const float DJAUTO_SLIDE_TAP_GUIDE_DELAY_SEC = 3 * FRAME_LENGTH_SEC;

        public const float DJAUTO_SLIDE_RELEASE_DELAY_SEC = 6 * FRAME_LENGTH_SEC;



        public const float BUTTON_HIT_RENDER_RADIUS = 0.4f;

        public bool ShowHand
        {
            get => InputData.ShowHand;
            set => InputData.ShowHand = value;
        }
        RenderGroup<HitRenderData> _hitGroup;
        bool _isHitGroupLocked;

        public InputManager()
        {
            _inputManager = this;
            DJAUTO_AUTOPLAY_START_SEC_SS.Data = -0.013f;
            //get sensor positions
            for (var i = 0; i < SENSOR_COUNT; i++)
            {
                InputData.SensorWorldPositions[i] = MajPos.GetSensorWorldPos((SensorType)i);
            }
            //REMEMBER TO FORCE INCLUDE
            var matHit = new Material(Shader.Find("Custom/Hit"));
            var hitMesh = MeshGenerator.CreateCircleMesh(8, 1f, true);
            _hitGroup = new(matHit, hitMesh, 6); // priority larger than notes
        }

        public unsafe void BeginHandler()
        {
            // UPDATE MUST BE EARLIER THAN NoteManager's UPDATE!!
            // (set in Script Execution Order)
            _isHitGroupLocked = ShowHand;
            if (_isHitGroupLocked)
            {
                _hitGroup.AdvanceWrite();
                var hitRender = _hitGroup.LockForWrite();
                _hitGroup.ResetCount();

                InputData.hitRender = (HitRenderData*)hitRender.GetUnsafePtr();
                InputData.HitWriteCountPtr = _hitGroup.WriteCountPtr;
            }
            InputData.BeginHandler(_isHitGroupLocked);

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                CheckButton(keyboard);
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.isPressed)
                {
                    CheckScreenPos(mouse.position.ReadValue());
                }
            }

            var touchscreen = Touchscreen.current;
            if (touchscreen != null)
            {
                foreach (var touch in touchscreen.touches)
                {
                    var phase = touch.phase.ReadValue();
                    if (phase == TouchPhase.None) continue;
                    if (phase is TouchPhase.Began or TouchPhase.Moved or TouchPhase.Stationary)
                        CheckScreenPos(touch.position.ReadValue());
                }
            }
        }

        // wait for slide and other notes finish update
        public void EndHandler()
        {
            InputData.EndHandler();
            if (_isHitGroupLocked)
            {
                _hitGroup.UnlockWrite();
                _hitGroup.Render();
                _hitGroup.Swap();
                _isHitGroupLocked = false;
            }
        }

        private void CheckButton(Keyboard keyboard)
        {
            InputData.HandleButtonInput(SensorType.A1, keyboard[Key.W].isPressed);
            InputData.HandleButtonInput(SensorType.A2, keyboard[Key.E].isPressed);
            InputData.HandleButtonInput(SensorType.A3, keyboard[Key.D].isPressed);
            InputData.HandleButtonInput(SensorType.A4, keyboard[Key.C].isPressed);
            InputData.HandleButtonInput(SensorType.A5, keyboard[Key.X].isPressed);
            InputData.HandleButtonInput(SensorType.A6, keyboard[Key.Z].isPressed);
            InputData.HandleButtonInput(SensorType.A7, keyboard[Key.A].isPressed);
            InputData.HandleButtonInput(SensorType.A8, keyboard[Key.Q].isPressed);
        }
        private void CheckScreenPos(Vector2 screenPos)
        {
            var mainCamera = Camera.main;
            var pos = (Vector2)mainCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 10f));

            InputData.HandleWorldPosInput(pos);
        }



        public void ResetState()
        {
        }

        public void OnDestroy()
        {
            _hitGroup?.Dispose();
        }
    }

    [BurstCompile]
    public unsafe struct InputDataB
    {
        public bool ShowHand;
        bool _showHandThisFrame;

        public NativeArray<float2> SensorWorldPositions;

        NativeArray<SensorState> _buttonStates;
        NativeArray<SensorState> _sensorStates;
        NativeArray<int> _buttonActiveDownNextFrame;
        NativeArray<int> _sensorActiveDownNextFrame;
        NativeArray<int> _nextButtonIndex;
        NativeArray<int> _nextSensorIndex;
        NativeArray<int> _nextButtonIndexNextFrame;
        NativeArray<int> _nextSensorIndexNextFrame;

        NativeArray<HitRenderData> _worldPosHitsNextFrame;
        int _worldPosHitsNextFrameCount;

        [NativeDisableUnsafePtrRestriction]
        public HitRenderData* hitRender;
        [NativeDisableUnsafePtrRestriction]
        public int* HitWriteCountPtr;

        public void Init()
        {
            SensorWorldPositions = new(SENSOR_COUNT, Allocator.Persistent);

            _buttonStates = new(BUTTON_COUNT, Allocator.Persistent);
            _sensorStates = new(SENSOR_COUNT, Allocator.Persistent);
            _buttonActiveDownNextFrame = new(BUTTON_COUNT, Allocator.Persistent);
            _sensorActiveDownNextFrame = new(SENSOR_COUNT, Allocator.Persistent);
            _nextButtonIndex = new(BUTTON_COUNT, Allocator.Persistent);
            _nextSensorIndex = new(SENSOR_COUNT, Allocator.Persistent);
            _nextButtonIndexNextFrame = new(BUTTON_COUNT, Allocator.Persistent);
            _nextSensorIndexNextFrame = new(SENSOR_COUNT, Allocator.Persistent);

            for (var i = 0; i < BUTTON_COUNT; i++)
                _buttonStates[i] = new();
            for (var i = 0; i < SENSOR_COUNT; i++)
                _sensorStates[i] = new();

            _worldPosHitsNextFrame = new(32, Allocator.Persistent);
        }






        // ==========button/sensor management==========
        // 上帧 DJAuto 缓冲 -> 本帧状态 -> 叠加用户输入 -> 判定 -> DJAuto 写入下帧缓冲

        public readonly SensorState GetButtonState(SensorType type) => _buttonStates[(int)type];
        public readonly SensorState GetSensorState(SensorType type) => _sensorStates[(int)type];


        // ======User Input Part======

        public void BeginHandler(bool showHandThisFrame)
        {
            _showHandThisFrame = showHandThisFrame;

            var hitCount = math.min(
                Interlocked.Exchange(ref _worldPosHitsNextFrameCount, 0),
                _worldPosHitsNextFrame.Length);
            if (_showHandThisFrame)
            {
                for (int i = 0; i < hitCount; i++)
                {
                    var hitIndex = Interlocked.Increment(ref *HitWriteCountPtr) - 1;
                    hitRender[hitIndex] = _worldPosHitsNextFrame[i];
                }
            }

            // 先保留上一帧的合计引用数，再消费 DJAuto 在上一帧排入的输入。
            // 随后用户输入会继续加到 ActiveDown 上，两种来源自然遵循同一套边沿判断。
            for (int i = 0; i < BUTTON_COUNT; i++)
            {
                ref var button = ref _buttonStates.ElementRef(i);
                button.LastActiveDown = button.ActiveDown;
                button.ActiveDown = Interlocked.Exchange(
                    ref _buttonActiveDownNextFrame.ElementRef(i), 0);
            }
            for (int i = 0; i < SENSOR_COUNT; i++)
            {
                ref var sensor = ref _sensorStates.ElementRef(i);
                sensor.LastActiveDown = sensor.ActiveDown;
                sensor.ActiveDown = Interlocked.Exchange(
                    ref _sensorActiveDownNextFrame.ElementRef(i), 0);
            }
        }

        /// <summary>
        /// 处理按键输入
        /// </summary>
        /// <param name="nextFrame">是否应用到下一帧（DJAuto）</param>
        public void HandleButtonInput(SensorType type, bool status, bool nextFrame = false)
        {
            if (!status) return;

            if (nextFrame)
                SetNextFrameButtonOn(type);
            else
                SetThisFrameButtonOn(type);
        }
        /// <summary>
        /// 处理世界坐标（手）输入
        /// </summary>
        /// <param name="nextFrame">是否应用到下一帧（DJAuto）</param>
        public void HandleWorldPosInput(in float2 pos, float radius = InputManager.DEFAULT_HAND_RADIUS, bool nextFrame = false)
        {
            for (int i = 0; i < SensorWorldPositions.Length; i++)
            {
                var combinedR = radius + MajPos.GetSensorRadius((SensorType)i);
                var combinedSq = combinedR * combinedR;
                ref readonly var sp = ref SensorWorldPositions.ElementRef(i);
                var dx = pos.x - sp.x;
                var dy = pos.y - sp.y;
                var distSq = dx * dx + dy * dy;

                if (distSq <= combinedSq)
                {
                    if (nextFrame)
                        SetNextFrameSensorOn((SensorType)i);
                    else
                        SetThisFrameSensorOn((SensorType)i);
                }
            }

            if (_showHandThisFrame) // 本帧没有锁定渲染缓冲时不能写入指针
            {
                var hit = new HitRenderData
                {
                    pos = pos,
                    radius = radius,
                    color = new float4(1, 0, 0, 0.75f)
                };

                if (nextFrame)
                {
                    var idx = Interlocked.Increment(ref _worldPosHitsNextFrameCount) - 1;
                    if (idx < _worldPosHitsNextFrame.Length)
                        _worldPosHitsNextFrame[idx] = hit;
                }
                else
                {
                    var idx = Interlocked.Increment(ref *HitWriteCountPtr) - 1;
                    hitRender[idx] = hit;
                }
            }
        }

        private void SetThisFrameButtonOn(SensorType type)
        {
            ref var button = ref _buttonStates.ElementRef((int)type);
            Interlocked.Increment(ref button.ActiveDown);
        }
        private void SetThisFrameSensorOn(SensorType type)
        {
            ref var sensor = ref _sensorStates.ElementRef((int)type);
            Interlocked.Increment(ref sensor.ActiveDown);
        }
        private void SetNextFrameButtonOn(SensorType type)
        {
            Interlocked.Increment(ref _buttonActiveDownNextFrame.ElementRef((int)type));
        }
        private void SetNextFrameSensorOn(SensorType type)
        {
            Interlocked.Increment(ref _sensorActiveDownNextFrame.ElementRef((int)type));
        }

        public void EndHandler()
        {
            if (_showHandThisFrame)
            {
                for (int i = 0; i < BUTTON_COUNT; i++)
                {
                    if (_buttonStates[i].Status)
                    {
                        var idx = Interlocked.Increment(ref *HitWriteCountPtr) - 1;
                        hitRender[idx] = new HitRenderData
                        {
                            pos = MajPos.GetBtnPos(i),
                            radius = InputManager.BUTTON_HIT_RENDER_RADIUS,
                            color = new float4(0, 1, 1, 0.5f) // Cyan responsive color
                        };
                    }
                }
                for (int i = 0; i < SENSOR_COUNT; i++)
                {
                    if (_sensorStates[i].Status)
                    {
                        var idx = Interlocked.Increment(ref *HitWriteCountPtr) - 1;
                        hitRender[idx] = new HitRenderData
                        {
                            pos = SensorWorldPositions[i],
                            radius = MajPos.GetSensorRadius((SensorType)i),
                            color = new float4(0, 1, 1, 0.5f) // Cyan responsive color
                        };
                    }
                }
            }
        }


        // ==========judge management==========
        public readonly void NextTapHold(SensorType pos)
        {
            Interlocked.Increment(ref _nextButtonIndexNextFrame.ElementRef((int)pos));
            Interlocked.Increment(ref _nextSensorIndexNextFrame.ElementRef((int)pos));
        }
        public readonly void NextTouch(SensorType pos)
        {
            Interlocked.Increment(ref _nextSensorIndexNextFrame.ElementRef((int)pos));
        }
        public readonly bool CanJudgeButton(SensorType pos, int order)
        {
            return order == _nextButtonIndex[(int)pos];
        }
        public readonly bool CanJudgeSensor(SensorType pos, int order)
        {
            return order == _nextSensorIndex[(int)pos];
        }


        public readonly void ApplyNextIndices()
        {
            for (int i = 0; i < BUTTON_COUNT; i++)
            {
                _nextButtonIndex.ElementRef(i) = _nextButtonIndexNextFrame[i];
            }
            for (int i = 0; i < SENSOR_COUNT; i++)
            {
                _nextSensorIndex.ElementRef(i) = _nextSensorIndexNextFrame[i];
            }
        }



        public void ResetState()
        {
            _worldPosHitsNextFrameCount = 0;

            for (var i = 0; i < BUTTON_COUNT; i++)
            {
                _buttonStates[i] = default;
                _buttonActiveDownNextFrame[i] = 0;
                _nextButtonIndex[i] = 0;
                _nextButtonIndexNextFrame[i] = 0;
            }
            for (var i = 0; i < SENSOR_COUNT; i++)
            {
                _sensorStates[i] = default;
                _sensorActiveDownNextFrame[i] = 0;
                _nextSensorIndex[i] = 0;
                _nextSensorIndexNextFrame[i] = 0;
            }
        }

        public void Dispose()
        {
            if (SensorWorldPositions.IsCreated) SensorWorldPositions.Dispose();
            if (_sensorStates.IsCreated) _sensorStates.Dispose();
            if (_sensorActiveDownNextFrame.IsCreated) _sensorActiveDownNextFrame.Dispose();
            if (_nextSensorIndex.IsCreated) _nextSensorIndex.Dispose();
            if (_nextSensorIndexNextFrame.IsCreated) _nextSensorIndexNextFrame.Dispose();
            if (_buttonStates.IsCreated) _buttonStates.Dispose();
            if (_buttonActiveDownNextFrame.IsCreated) _buttonActiveDownNextFrame.Dispose();
            if (_nextButtonIndex.IsCreated) _nextButtonIndex.Dispose();
            if (_nextButtonIndexNextFrame.IsCreated) _nextButtonIndexNextFrame.Dispose();

            if (_worldPosHitsNextFrame.IsCreated) _worldPosHitsNextFrame.Dispose();
        }
    }

    public struct SensorState
    {
        public readonly bool Status => ActiveDown > 0;
        public readonly bool IsPadDown => LastActiveDown <= 0 && ActiveDown > 0;
        public readonly bool IsPadUp => LastActiveDown > 0 && ActiveDown <= 0;

        public int ActiveDown;
        public int LastActiveDown;
    }
}
