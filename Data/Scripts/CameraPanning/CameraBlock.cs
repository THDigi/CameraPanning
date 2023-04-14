using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace Digi.CameraPanning
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), useEntityUpdate: false)]
    public class CameraBlock : MyGameLogicComponent
    {
        public CameraClientside Client;

        public static float FovToWidth(float fovRadians) => 2 * (float)Math.Tan(fovRadians / 2d) * ZoomDistance;
        public static float WidthToFov(float width) => 2 * (float)Math.Atan(width / 2 / ZoomDistance);
        const float ZoomDistance = 1; // just for math to make sense

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            if(MyAPIGateway.Utilities.IsDedicated)
                return; // DS doesn't need any of this

            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if(MyAPIGateway.Utilities.IsDedicated)
                    return; // DS doesn't need any of this

                MyCubeBlock block = Entity as MyCubeBlock;
                if(block == null || block.IsPreview || block.CubeGrid.IsPreview || block.CubeGrid.Physics == null)
                    return; // ignore ghost grids

                Client = new CameraClientside(this);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Close()
        {
            try
            {
                Client?.Close();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void HandleZoom()
        {
            if(Client == null || !Client.IsValid)
                return;

            int scroll = MyAPIGateway.Input.DeltaMouseScrollWheelValue();

            if(scroll > 0)
            {
                Client.ZoomIn();
            }
            else if(scroll < 0)
            {
                Client.ZoomOut();
            }
        }
    }

    public class CameraClientside
    {
        readonly CameraBlock Logic;
        readonly MyCubeBlock Block;
        readonly ZoomLimits ZoomLimits;
        readonly MyCameraBlockDefinition BlockDef;

        readonly MyEntity3DSoundEmitter SoundRotateEmitter;
        readonly MyEntity3DSoundEmitter SoundZoomEmitter;

        Matrix OriginalMatrix;
        Matrix RotatedMatrix;
        Vector3 CustomOffset;
        Vector3 OriginalWorldOffsetAsLocal;

        bool Recenter = false;
        float CurrentPitch = 0;
        float CurrentYaw = 0;
        float CurrentRoll = 0;
        float CurrentSpeed = 0;

        int SoundRotateStopDelay = 0;
        int SoundZoomStopDelay = 0;

        int PrevFOV = 0;
        int IgnoreFovChangeForTicks = 0;

        float ZoomWidth;

        const float Epsilon = 0.0001f;
        const float RotationSpeedMul = 0.075f; // rotation input multiplier as it is too fast raw compared to the rest of the game
        const float RotationMaxSpeed = 2f; // using a max speed to feel like it's on actual servos
        const int ZoomSoundDelay = 30; // game ticks
        const float RotateSoundVolume = 1.0f;
        const float ZoomSoundVolume = 0.35f;
        static readonly MySoundPair RotateSound = new MySoundPair("ArcBlockRotor");
        static readonly MySoundPair ZoomSound = new MySoundPair("ArcWepShipGatlingRotation");

        public CameraClientside(CameraBlock logic)
        {
            Logic = logic;
            Block = (MyCubeBlock)Logic.Entity;
            BlockDef = (MyCameraBlockDefinition)Block.BlockDefinition;

            if(!CameraPanningMod.Instance.WidthLimits.TryGetValue(BlockDef.Id, out ZoomLimits))
            {
                Log.Error($"{BlockDef.Id.ToString()} didn't exist at BeforeStart() time so it has no stored limits! What is going on?!", Log.PRINT_MESSAGE);
                return;
            }

            ZoomWidth = CameraBlock.FovToWidth(MathHelper.ToRadians(60));

            OriginalMatrix = Block.PositionComp.LocalMatrixRef;
            RotatedMatrix = OriginalMatrix;

            Vector3D worldViewPosition = MatrixD.Invert(Block.GetViewMatrix()).Translation;
            OriginalWorldOffsetAsLocal = Vector3D.Transform(worldViewPosition, MatrixD.Invert(Block.WorldMatrix)); // to local

            // HACK temporary fix for camera being in the center of the block
            TryFixCameraPosition(); // moves the camera view's position towards the default mount point by gridSize/2.

            SoundRotateEmitter = new MyEntity3DSoundEmitter(Block);
            SoundRotateEmitter.CustomVolume = RotateSoundVolume;

            SoundZoomEmitter = new MyEntity3DSoundEmitter(Block);
            SoundZoomEmitter.CustomVolume = ZoomSoundVolume;
        }

        public void Close()
        {
            if(SoundRotateEmitter != null)
                SoundRotateEmitter.StopSound(true, true);

            if(SoundZoomEmitter != null)
                SoundZoomEmitter.StopSound(true, true);
        }

        public bool IsValid => (Block != null && Block.CubeGrid.Physics.Enabled);

        public void ZoomIn()
        {
            ZoomWidth *= 0.9f;
            UpdateZoom();
        }

        public void ZoomOut()
        {
            ZoomWidth *= 1.1f;
            UpdateZoom();
        }

        void UpdateZoom()
        {
            ZoomWidth = MathHelper.Clamp(ZoomWidth, ZoomLimits.MinWidth, ZoomLimits.MaxWidth);

            float fov = CameraBlock.WidthToFov(ZoomWidth);
            BlockDef.MinFov = fov;
            BlockDef.MaxFov = fov;
        }

        public void Update()
        {
            //if(!IsValid)
            if(!(Block != null && Block.CubeGrid.Physics.Enabled))
                return;

            bool rotating = UpdateCamera(); // returns true if the camera is rotating, false otherwise

            if(SoundRotateEmitter != null)
            {
                if(rotating && SoundRotateStopDelay > 0) // reset the stop delay
                    SoundRotateStopDelay = 0;

                if(SoundRotateEmitter.IsPlaying) // dynamically adjust sound volume depending on last rotation speed
                    SoundRotateEmitter.CustomVolume = RotateSoundVolume * MathHelper.Clamp(CurrentSpeed / RotationMaxSpeed, 0, 1);

                if(!rotating && SoundRotateEmitter != null && SoundRotateEmitter.IsPlaying)
                {
                    if(SoundRotateStopDelay == 0)
                        SoundRotateStopDelay = ZoomSoundDelay;

                    if(--SoundRotateStopDelay == 0)
                        SoundRotateEmitter.StopSound(true);
                }
            }
        }

        public void EnterView()
        {
            OriginalMatrix = Block.PositionComp.LocalMatrixRef; // recalculate original matrix and rotated matrix in case the block was "moved" (by merge or who knows what else)
            RotateCamera(0, 0, 0, true);

            float FovRad = MyAPIGateway.Session.Camera.FovWithZoom;
            int FOV = (int)Math.Round(MathHelper.ToDegrees(FovRad), 0);

            PrevFOV = FOV;
            ZoomWidth = CameraBlock.FovToWidth(FovRad);
            IgnoreFovChangeForTicks = 2;

            string lookaround = GetControlAssignedName(MyAPIGateway.Input.GetGameControl(MyControlsSpace.LOOKAROUND));
            string rotationType = GetControlAssignedName(MyAPIGateway.Input.GetGameControl(MyControlsSpace.SPRINT));
            string cameraMode = GetControlAssignedName(MyAPIGateway.Input.GetGameControl(MyControlsSpace.CAMERA_MODE));
            string text = $"Hold [{lookaround}] to pan camera, [{lookaround}]+[{rotationType}] to change rotation type and [{cameraMode}] to reset.";

            Notify(text, 3000);
        }

        public void ExitView()
        {
            CameraPanningMod.Instance.Notification.Hide();

            Block.PositionComp.SetLocalMatrix(ref OriginalMatrix); // reset the camera's matrix to avoid seeing its model skewed if the model gets updated with the local matrix
            Block.Render.UpdateRenderObject(true); // force model to be recalculated to avoid invisible models on merge/unmerge while camera is viewed

            // reset definition on exit camera for allowing mods to read proper values
            BlockDef.MinFov = ZoomLimits.MinFov;
            BlockDef.MaxFov = ZoomLimits.MaxFov;

            if(SoundZoomEmitter != null)
                SoundZoomEmitter.StopSound(true);
        }

        bool UpdateCamera()
        {
            float FovRad = MyAPIGateway.Session.Camera.FovWithZoom;
            int FOV = (int)Math.Round(MathHelper.ToDegrees(FovRad), 0);

            // takes a few ticks for view to change to camera...
            if(IgnoreFovChangeForTicks > 0)
            {
                IgnoreFovChangeForTicks--;
                PrevFOV = FOV;
                ZoomWidth = CameraBlock.FovToWidth(FovRad);
            }

            if(Math.Abs(FOV - PrevFOV) > 0)
            {
                PrevFOV = FOV;

                if(SoundZoomEmitter != null && !SoundZoomEmitter.IsPlaying)
                {
                    SoundZoomEmitter.PlaySound(ZoomSound, stopPrevious: true, skipIntro: true, alwaysHearOnRealistic: true, force2D: true);
                    SoundZoomStopDelay = ZoomSoundDelay;
                }

                Notify(FOV.ToString("0°"), 300);
            }
            else
            {
                if(SoundZoomEmitter != null && SoundZoomEmitter.IsPlaying)
                {
                    if(SoundZoomStopDelay == 0 || --SoundZoomStopDelay == 0)
                        SoundZoomEmitter.StopSound(true);
                }
            }

            if(!MyAPIGateway.Gui.ChatEntryVisible && !MyAPIGateway.Gui.IsCursorVisible)
            {
                IMyControl lookaroundControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.LOOKAROUND);
                IMyControl rotationTypeControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.SPRINT);
                IMyControl cameraModeControl = MyAPIGateway.Input.GetGameControl(MyControlsSpace.CAMERA_MODE);

                if(!Recenter && cameraModeControl.IsNewPressed())
                {
                    Recenter = true;
                    return false; // no reason to compute further
                }

                if(lookaroundControl.IsPressed())
                {
                    IMyHudNotification notif = CameraPanningMod.Instance.Notification;
                    if(notif.Text.Length > 10) // if it's the control hints, hide it
                        notif.Hide();

                    Vector2 rot = MyAPIGateway.Input.GetRotation();
                    bool rollToggle = rotationTypeControl.IsPressed();

                    if(Math.Abs(rot.X) > Epsilon || Math.Abs(rot.Y) > Epsilon)
                    {
                        if(Recenter)
                            Recenter = false;

                        if(rollToggle)
                        {
                            // slowly reset yaw while giving control to roll
                            if(RotateCamera(rot.X * RotationSpeedMul, MathHelper.Lerp(CurrentYaw, 0f, 0.1f), rot.Y * RotationSpeedMul))
                                return true;
                        }
                        else
                        {
                            if(RotateCamera(rot.X * RotationSpeedMul, rot.Y * RotationSpeedMul, 0))
                                return true;
                        }
                    }
                    else if(rollToggle && Math.Abs(CurrentYaw) > Epsilon) // not moving mouse but holding modifier should result in yaw being slowly reset to 0
                    {
                        if(RotateCamera(0, MathHelper.Lerp(CurrentYaw, 0f, 0.1f), 0))
                            return true;
                    }
                }
            }

            if(Recenter)
            {
                if(Math.Abs(CurrentPitch) > Epsilon || Math.Abs(CurrentYaw) > Epsilon || Math.Abs(CurrentRoll) > Epsilon)
                {
                    if(RotateCamera(MathHelper.Lerp(CurrentPitch, 0f, 0.1f), MathHelper.Lerp(CurrentYaw, 0f, 0.1f), MathHelper.Lerp(CurrentRoll, 0f, 0.1f)))
                        return true;
                }
                else
                {
                    Recenter = false;

                    // ensure it's perfectly centered
                    CurrentPitch = 0;
                    CurrentYaw = 0;
                    CurrentRoll = 0;

                    Block.PositionComp.SetLocalMatrix(ref RotatedMatrix);
                    return true;
                }
            }

            return false;
        }

        bool RotateCamera(float pitchMod, float yawMod, float rollMod, bool forceRecalculate = false)
        {
            IMyCameraBlock camera = (IMyCameraBlock)Block;
            float angleLimit = camera.RaycastConeLimit;

            pitchMod = MathHelper.Clamp(pitchMod, -RotationMaxSpeed, RotationMaxSpeed);
            yawMod = MathHelper.Clamp(yawMod, -RotationMaxSpeed, RotationMaxSpeed);
            rollMod = MathHelper.Clamp(rollMod, -RotationMaxSpeed, RotationMaxSpeed);

            float setPitch = ClampAngle(CurrentPitch - pitchMod, angleLimit);
            float setYaw = ClampAngle(CurrentYaw - yawMod, angleLimit);
            float setRoll = ClampAngle(CurrentRoll - rollMod);

            if(forceRecalculate || Math.Abs(setPitch - CurrentPitch) >= Epsilon || Math.Abs(setYaw - CurrentYaw) >= Epsilon || Math.Abs(setRoll - CurrentRoll) >= Epsilon)
            {
                CurrentSpeed = new Vector3(pitchMod, yawMod, rollMod).Length();
                CurrentPitch = setPitch;
                CurrentYaw = setYaw;
                CurrentRoll = setRoll;

                Matrix roll = MatrixD.CreateFromYawPitchRoll(0, 0, MathHelper.ToRadians(CurrentRoll));
                Matrix yawAndPitch = MatrixD.CreateFromYawPitchRoll(MathHelper.ToRadians(CurrentYaw), MathHelper.ToRadians(CurrentPitch), 0);

                Matrix rotated = yawAndPitch * roll * OriginalMatrix;

                // counter-shift the position so that it stays off-center where it needs to be
                Vector3 rotatedOffset = Vector3.TransformNormal(OriginalWorldOffsetAsLocal, rotated);
                Vector3 originalOffset = Vector3.TransformNormal(OriginalWorldOffsetAsLocal, OriginalMatrix);
                Vector3 dir = originalOffset - rotatedOffset;

                rotated.Translation += dir;

                rotated.Translation += CustomOffset;

                RotatedMatrix = rotated;
                Block.PositionComp.SetLocalMatrix(ref rotated);

                if(!forceRecalculate && SoundRotateEmitter != null && !SoundRotateEmitter.IsPlaying)
                    SoundRotateEmitter.PlaySound(RotateSound, stopPrevious: true, skipIntro: true, alwaysHearOnRealistic: true, force2D: true);

                return true;
            }

            return false;
        }

        void TryFixCameraPosition()
        {
            // ignore mods and blocks that use ModelOffset
            if(BlockDef?.Context == null || !BlockDef.Context.IsBaseGame || BlockDef.ModelOffset.LengthSquared() > Epsilon)
                return;

            IMyCubeBlock b = (IMyCubeBlock)Block;
            Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
            b.Model.GetDummies(dummies);

            foreach(IMyModelDummy dummy in dummies.Values)
            {
                // MyCameraBlock.GetViewMatrix()
                if(dummy.Name == MyCameraBlock.DUMMY_NAME_POSITION)
                {
                    // has custom position, don't offset it.
                    return;
                }
            }

            CustomOffset = OriginalMatrix.Backward * ((Block.CubeGrid.GridSize / 2f) - 0.05f);
        }

        static void Notify(string text, int aliveTimeMs)
        {
            IMyHudNotification notification = CameraPanningMod.Instance.Notification;

            if(notification == null)
                notification = CameraPanningMod.Instance.Notification = MyAPIGateway.Utilities.CreateNotification("");

            notification.Hide(); // required since SE v1.194
            notification.AliveTime = aliveTimeMs;
            notification.Text = text;
            notification.Show();
        }

        static float ClampAngle(float value, float limit = 0)
        {
            if(limit > 0)
                value = MathHelper.Clamp(value, -limit, limit);

            if(value > 180)
                value = -180 + (value - 180);
            else if(value < -180)
                value = 180 - (value - 180);

            return value;
        }

        static string GetControlAssignedName(IMyControl control)
        {
            string assign = control.GetControlButtonName(MyGuiInputDeviceEnum.Mouse);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            assign = control.GetControlButtonName(MyGuiInputDeviceEnum.Keyboard);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            assign = control.GetControlButtonName(MyGuiInputDeviceEnum.KeyboardSecond);

            if(!string.IsNullOrWhiteSpace(assign))
                return assign;

            return "(unassigned)";
        }
    }
}
