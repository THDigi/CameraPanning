using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRageMath;

namespace Digi.CameraPanning
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class CameraPanningMod : MySessionComponentBase
    {
        public static CameraPanningMod Instance = null;
        public IMyHudNotification Notification;
        public Dictionary<MyDefinitionId, ZoomLimits> WidthLimits;

        Dictionary<MyCameraBlockDefinition, float> OriginalDefData;

        public const float CameraNewMaxFOV = (float)(100 / 180d * Math.PI); // 100 degrees in radians

        public override void LoadData()
        {
            Instance = this;
            Log.ModName = "Camera Panning";
            Log.AutoClose = false;
        }

        public override void BeforeStart()
        {
            try
            {
                if(!MyAPIGateway.Utilities.IsDedicated)
                {
                    OriginalDefData = new Dictionary<MyCameraBlockDefinition, float>();

                    EditCamera(new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "SmallCameraBlock"));
                    EditCamera(new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "LargeCameraBlock"));
                    EditCamera(new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "SmallCameraTopMounted"));
                    EditCamera(new MyDefinitionId(typeof(MyObjectBuilder_CameraBlock), "LargeCameraTopMounted"));

                    StoreFovLimits();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        protected override void UnloadData()
        {
            try
            {
                if(OriginalDefData != null)
                {
                    // restore original FOV for camera definitions as they are not reloaded in between world loads which means removing the mod will not reset the FOV.
                    foreach(KeyValuePair<MyCameraBlockDefinition, float> kv in OriginalDefData)
                    {
                        kv.Key.MaxFov = kv.Value;
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }

            Instance = null;
            Log.Close();
        }

        void EditCamera(MyDefinitionId defId)
        {
            MyCubeBlockDefinition blockDef;
            if(!MyDefinitionManager.Static.TryGetCubeBlockDefinition(defId, out blockDef))
                return;

            MyCameraBlockDefinition camDef = blockDef as MyCameraBlockDefinition;
            if(camDef != null)
            {
                OriginalDefData[camDef] = camDef.MaxFov;
                camDef.MaxFov = CameraNewMaxFOV;
            }
        }

        void StoreFovLimits()
        {
            WidthLimits = new Dictionary<MyDefinitionId, ZoomLimits>(MyDefinitionId.Comparer);

            foreach(MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions())
            {
                MyCameraBlockDefinition camDef = def as MyCameraBlockDefinition;
                if(camDef != null)
                {
                    WidthLimits[def.Id] = new ZoomLimits(camDef.MinFov, camDef.MaxFov);
                }
            }
        }

        CameraBlock PrevCamera;

        public override void HandleInput()
        {
            try
            {
                if(MyAPIGateway.Utilities.IsDedicated)
                    return;

                CameraBlock cameraBlock = (MyAPIGateway.Session?.CameraController as IMyCameraBlock)?.GameLogic?.GetAs<CameraBlock>();

                if(cameraBlock != PrevCamera)
                {
                    PrevCamera?.Client?.ExitView();
                    PrevCamera = null;

                    cameraBlock?.Client?.EnterView();

                    PrevCamera = cameraBlock;
                }

                cameraBlock?.Client?.Update();

                if(MyAPIGateway.Gui.IsCursorVisible || MyAPIGateway.Gui.ChatEntryVisible)
                    return;

                HandleCameraZoom();
                HandleResetFirstPersonView();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void HandleCameraZoom()
        {
            IMyCameraBlock cameraBlock = MyAPIGateway.Session?.CameraController as IMyCameraBlock;
            CameraBlock logic = cameraBlock?.GameLogic?.GetAs<CameraBlock>();
            logic?.HandleZoom();
        }

        void HandleResetFirstPersonView()
        {
            // Reset view when forced in first person by pressing the camera key
            if(MyAPIGateway.Input.IsNewGameControlPressed(MyControlsSpace.CAMERA_MODE))
            {
                var camCtrl = MyAPIGateway.Session.CameraController;
                var controller = MyAPIGateway.Session.ControlledObject as Sandbox.Game.Entities.IMyControllableEntity; // avoiding ambiguity

                if(camCtrl == null || controller == null)
                    return;

                if(!MyAPIGateway.Session.SessionSettings.Enable3rdPersonView || controller.ForceFirstPersonCamera)
                {
                    if(controller is IMyShipController)
                    {
                        // HACK this is how MyCockpit.Rotate() does things so I kinda have to use these magic numbers.
                        float num = MyAPIGateway.Input.GetMouseSensitivity() * 0.13f;
                        camCtrl.Rotate(new Vector2(controller.HeadLocalXAngle / num, controller.HeadLocalYAngle / num), 0);
                    }
                    else
                    {
                        // HACK this is how MyCharacter.RotateHead() does things so I kinda have to use these magic numbers.
                        camCtrl.Rotate(new Vector2(controller.HeadLocalXAngle * 2, controller.HeadLocalYAngle * 2), 0);
                    }
                }
            }
        }
    }
}
