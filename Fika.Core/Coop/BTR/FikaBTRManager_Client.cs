﻿using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.GlobalEvents;
using EFT.InventoryLogic;
using EFT.Vehicle;
using Fika.Core.Coop.Components;
using Fika.Core.Coop.Players;
using Fika.Core.Networking;
using GPUInstancer;
using HarmonyLib;
using SPT.Custom.BTR;
using SPT.Custom.BTR.Utils;
using SPT.SinglePlayer.Utils.TraderServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

// TODO: Door animations don't play when Clients enter, but it does when the Host does for the Client?
// TODO: Door animations are because of the OnBtrStateChanged, need to use that and run it with a packet
// TODO: Sometimes you can't get out of the BTR if more than 2 people are in... no idea why

namespace Fika.Core.Coop.BTR
{
	/// <summary>
	/// Based on <see href="https://dev.sp-tarkov.com/SPT/Modules/src/branch/master/project/SPT.Custom/BTR/BTRManager.cs"/>
	/// </summary>
	internal class FikaBTRManager_Client : MonoBehaviour
	{
		private GameWorld gameWorld;
		private BotEventHandler botEventHandler;

		private BotBTRService btrBotService;
		private BTRControllerClass btrController;
		private BTRVehicle btrServerSide;
		public BTRView btrClientSide;
		private BTRDataPacket btrDataPacket = default;
#pragma warning disable CS0414 // The field 'FikaBTRManager_Client.btrInitialized' is assigned but its value is never used
		private bool btrInitialized = false;
#pragma warning restore CS0414 // The field 'FikaBTRManager_Client.btrInitialized' is assigned but its value is never used
		private bool btrBotShooterInitialized = false;

		private BTRTurretServer btrTurretServer;
		private bool isTurretInDefaultRotation;
		private BulletClass btrMachineGunAmmo;
		private Item btrMachineGunWeapon;
		private Player.FirearmController firearmController;
		private WeaponSoundPlayer weaponSoundPlayer;

		private MethodInfo _updateTaxiPriceMethod;

		private float originalDamageCoeff;

		private FikaClient client;
		public Queue<BTRPacket> btrPackets = new(60);
		private string botShooterId = string.Empty;
		private ManualLogSource btrLogger;

		FikaBTRManager_Client()
		{
			Type btrControllerType = typeof(BTRControllerClass);
			_updateTaxiPriceMethod = AccessTools.GetDeclaredMethods(btrControllerType).Single(IsUpdateTaxiPriceMethod);
			client = Singleton<FikaClient>.Instance;
			btrLogger = BepInEx.Logging.Logger.CreateLogSource("BTR Client");
		}

		private async void Awake()
		{
			try
			{
				gameWorld = Singleton<GameWorld>.Instance;
				if (gameWorld == null)
				{
					Destroy(this);
					return;
				}

				if (gameWorld.BtrController == null)
				{
					gameWorld.BtrController = new BTRControllerClass();
				}

				btrController = gameWorld.BtrController;

				await InitBtr();
			}
			catch
			{
				btrLogger.LogError("[SPT-BTR] Unable to spawn BTR. Check logs.");
				Destroy(this);
				throw;
			}
		}

		public void OnPlayerInteractDoor(Player player, PlayerInteractPacket interactPacket)
		{
			// If the player is going into the BTR, store their damage coefficient
			// and set it to 0, so they don't die while inside the BTR
			if (interactPacket.InteractionType == EInteractionType.GoIn && player.IsYourPlayer)
			{
				originalDamageCoeff = player.ActiveHealthController.DamageCoeff;
				player.ActiveHealthController.SetDamageCoeff(0f);

			}
			// Otherwise restore the damage coefficient
			else if (interactPacket.InteractionType == EInteractionType.GoOut && player.IsYourPlayer)
			{
				player.ActiveHealthController.SetDamageCoeff(originalDamageCoeff);
			}
		}

		// Find `BTRControllerClass.method_9(PathDestination currentDestinationPoint, bool lastRoutePoint)`
		private bool IsUpdateTaxiPriceMethod(MethodInfo method)
		{
			return method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(PathDestination);
		}

		private void Update()
		{
			if (btrPackets.Count > 0)
			{
				BTRPacket packet = btrPackets.Dequeue();
				if (packet.HasBotProfileId)
				{
					AttachBot(packet.BotNetId);
				}
				if (packet.HasShot)
				{
					ObservedShot(packet.ShotPosition, packet.ShotDirection);
				}
				btrDataPacket = packet.BTRDataPacket;
			}

			if (!btrBotShooterInitialized)
			{
				InitBtrBotService();
				btrBotShooterInitialized = true;
			}

			btrController.SyncBTRVehicleFromServer(btrDataPacket);

			if (!isTurretInDefaultRotation)
			{
				btrTurretServer.DisableAiming();
			}
		}

		private void ObservedShot(Vector3 position, Vector3 direction)
		{
			gameWorld.SharedBallisticsCalculator.Shoot(btrMachineGunAmmo, position, direction, botShooterId, btrMachineGunWeapon, 1f, 0);
			firearmController.method_54(weaponSoundPlayer, btrMachineGunAmmo, position, direction, false);
		}

		public void AttachBot(int netId)
		{
			if (CoopHandler.TryGetCoopHandler(out CoopHandler coopHandler))
			{
				if (coopHandler.Players.TryGetValue(netId, out CoopPlayer player))
				{
					BTRTurretView turretView = btrClientSide.turret;

					player.Transform.Original.position = turretView.BotRoot.position;
					player.PlayerBones.Weapon_Root_Anim.SetPositionAndRotation(turretView.GunRoot.position, turretView.GunRoot.rotation);

					WeaponPrefab weaponPrefab = player.HandsController.ControllerGameObject.GetComponent<WeaponPrefab>();
					if (weaponPrefab != null)
					{
						weaponSoundPlayer = weaponPrefab.GetComponent<WeaponSoundPlayer>();

						Transform weaponTransform = weaponPrefab.Hierarchy.GetTransform(ECharacterWeaponBones.weapon);
						if (weaponTransform != null)
						{
							weaponPrefab.transform.SetPositionAndRotation(turretView.GunRoot.position, turretView.GunRoot.rotation);
							weaponTransform.SetPositionAndRotation(turretView.GunRoot.position, turretView.GunRoot.rotation);

							string[] gunModsToDisable = Traverse.Create(turretView).Field<string[]>("_gunModsToDisable").Value;
							if (gunModsToDisable != null)
							{
								foreach (Transform child in weaponTransform)
								{
									if (gunModsToDisable.Contains(child.name))
									{
										child.gameObject.SetActive(false);
									}
								}
							}
						}
						else
						{
							btrLogger.LogError("AttachBot: WeaponTransform was null!");
						}
					}
					else
					{
						btrLogger.LogError("AttachBot: WeaponPrefab was null!");
					}

					if (player.HealthController.IsAlive)
					{
						player.BodyAnimatorCommon.enabled = false;
						if (player.HandsController.FirearmsAnimator != null)
						{
							player.HandsController.FirearmsAnimator.Animator.enabled = false;
						}

						PlayerPoolObject component = player.gameObject.GetComponent<PlayerPoolObject>();
						foreach (Collider collider in component.Colliders)
						{
							collider.enabled = false;
						}

						List<Renderer> rendererList = new(256);
						player.PlayerBody.GetRenderersNonAlloc(rendererList);
						if (weaponPrefab != null)
						{
							rendererList.AddRange(weaponPrefab.Renderers);
						}
						rendererList.ForEach(renderer => renderer.forceRenderingOff = true);
					}

					firearmController = (Player.FirearmController)player.HandsController;

					btrBotShooterInitialized = true;
					botShooterId = player.ProfileId;
				}
			}
			else
			{
				btrLogger.LogError("AttachBot: CoopHandler was null!");
			}
		}

		private async Task InitBtr()
		{
			// Initial setup
			await btrController.InitBtrController();

			botEventHandler = Singleton<BotEventHandler>.Instance;
			BotsController botsController = Singleton<IBotGame>.Instance.BotsController;
			btrBotService = botsController.BotTradersServices.BTRServices;
			btrController.method_3(); // spawns server-side BTR game object
									  //botsController.BotSpawner.SpawnBotBTR(); // spawns the scav bot which controls the BTR's turret

			// Initial BTR configuration
			btrServerSide = btrController.BtrVehicle;
			btrClientSide = btrController.BtrView;
			btrServerSide.transform.Find("KillBox").gameObject.AddComponent<BTRRoadKillTrigger>();

			// Get config from server and initialise respective settings
			ConfigureSettingsFromServer();

			/*var btrMapConfig = btrController.MapPathsConfiguration;
            if (btrMapConfig == null)
            {
                ConsoleScreen.LogError($"{nameof(btrController.MapPathsConfiguration)}");
                return;
            }
            btrServerSide.CurrentPathConfig = btrMapConfig.PathsConfiguration.pathsConfigurations.RandomElement();
            btrServerSide.Initialization(btrMapConfig);*/
			btrController.method_14(); // creates and assigns the BTR a fake stash

			DisableServerSideObjects();

			/*gameWorld.MainPlayer.OnBtrStateChanged += HandleBtrDoorState;*/

			/*btrServerSide.MoveEnable();*/
			btrServerSide.IncomingToDestinationEvent += ToDestinationEvent;

			// Sync initial position and rotation
			UpdateDataPacket();
			btrClientSide.transform.position = btrDataPacket.position;
			btrClientSide.transform.rotation = btrDataPacket.rotation;

			// Initialise turret variables
			btrTurretServer = btrServerSide.BTRTurret;
			Transform btrTurretDefaultTargetTransform = (Transform)AccessTools.Field(btrTurretServer.GetType(), "defaultTargetTransform").GetValue(btrTurretServer);
			isTurretInDefaultRotation = btrTurretServer.targetTransform == btrTurretDefaultTargetTransform
				&& btrTurretServer.targetPosition == btrTurretServer.defaultAimingPosition;
			btrMachineGunAmmo = (BulletClass)BTRUtil.CreateItem(BTRUtil.BTRMachineGunAmmoTplId);
			btrMachineGunWeapon = BTRUtil.CreateItem(BTRUtil.BTRMachineGunWeaponTplId);

			// Pull services data for the BTR from the server
			TraderServicesManager.Instance.GetTraderServicesDataFromServer(BTRUtil.BTRTraderId);

			btrInitialized = true;
		}

		private void ConfigureSettingsFromServer()
		{
			SPT.Custom.BTR.Models.BTRConfigModel serverConfig = BTRUtil.GetConfigFromServer();

			btrServerSide.moveSpeed = serverConfig.MoveSpeed;
			btrServerSide.pauseDurationRange.x = serverConfig.PointWaitTime.Min;
			btrServerSide.pauseDurationRange.y = serverConfig.PointWaitTime.Max;
			btrServerSide.readyToDeparture = serverConfig.TaxiWaitTime;
		}

		private void InitBtrBotService()
		{
			//btrBotService.Reset(); // Player will be added to Neutrals list and removed from Enemies list
			TraderServicesManager.Instance.OnTraderServicePurchased += BtrTraderServicePurchased;
		}

		/**
         * BTR has arrived at a destination, re-calculate taxi prices and remove purchased taxi service
         */
		private void ToDestinationEvent(PathDestination destinationPoint, bool isFirst, bool isFinal, bool isLastRoutePoint)
		{
			// Remove purchased taxi service
			TraderServicesManager.Instance.RemovePurchasedService(ETraderServiceType.PlayerTaxi, BTRUtil.BTRTraderId);

			// Update the prices for the taxi service
			_updateTaxiPriceMethod.Invoke(btrController, [destinationPoint, isFinal]);

			// Update the UI
			TraderServicesManager.Instance.GetTraderServicesDataFromServer(BTRUtil.BTRTraderId);
		}

		public void DisplayNetworkNotification(ETraderServiceType serviceType)
		{
			if (gameWorld.MainPlayer.BtrState != EPlayerBtrState.Inside)
			{
				return;
			}

			int[] playerArray = [gameWorld.MainPlayer.Id];
			GlobalEventHandlerClass.CreateEvent<BtrServicePurchaseEvent>().Invoke(playerArray, serviceType);
		}

		private bool IsBtrService(ETraderServiceType serviceType)
		{
			if (serviceType == ETraderServiceType.BtrItemsDelivery || serviceType == ETraderServiceType.PlayerTaxi || serviceType == ETraderServiceType.BtrBotCover)
			{
				return true;
			}

			return false;
		}

		private void BtrTraderServicePurchased(ETraderServiceType serviceType, string subserviceId)
		{
			if (!IsBtrService(serviceType))
			{
				return;
			}

			BTRServicePacket packet = new(gameWorld.MainPlayer.ProfileId)
			{
				TraderServiceType = serviceType
			};

			if (!string.IsNullOrEmpty(subserviceId))
			{
				packet.HasSubservice = true;
				packet.SubserviceId = subserviceId;
			}

			client.SendData(ref packet, LiteNetLib.DeliveryMethod.ReliableOrdered);
		}

		private BTRDataPacket UpdateDataPacket()
		{
			btrDataPacket.position = btrServerSide.transform.position;
			btrDataPacket.rotation = btrServerSide.transform.rotation;
			if (btrTurretServer != null && btrTurretServer.gunsBlockRoot != null)
			{
				btrDataPacket.turretRotation = btrTurretServer.transform.localEulerAngles.y;
				btrDataPacket.gunsBlockRotation = btrTurretServer.gunsBlockRoot.localEulerAngles.x;
			}
			btrDataPacket.State = (byte)btrServerSide.BtrState;
			btrDataPacket.RouteState = (byte)btrServerSide.VehicleRouteState;
			btrDataPacket.LeftSideState = btrServerSide.LeftSideState;
			btrDataPacket.LeftSlot0State = btrServerSide.LeftSlot0State;
			btrDataPacket.LeftSlot1State = btrServerSide.LeftSlot1State;
			btrDataPacket.RightSideState = btrServerSide.RightSideState;
			btrDataPacket.RightSlot0State = btrServerSide.RightSlot0State;
			btrDataPacket.RightSlot1State = btrServerSide.RightSlot1State;
			btrDataPacket.currentSpeed = btrServerSide.currentSpeed;
			btrDataPacket.timeToEndPause = btrServerSide.timeToEndPause;
			btrDataPacket.moveDirection = (byte)btrServerSide.VehicleMoveDirection;
			btrDataPacket.MoveSpeed = btrServerSide.moveSpeed;
			if (btrController != null && btrController.BotShooterBtr != null)
			{
				btrDataPacket.BtrBotId = btrController.BotShooterBtr.Id;
			}

			return btrDataPacket;
		}

		private void DisableServerSideObjects()
		{
			MeshRenderer[] meshRenderers = btrServerSide.transform.GetComponentsInChildren<MeshRenderer>();
			foreach (MeshRenderer renderer in meshRenderers)
			{
				renderer.enabled = false;
			}

			btrServerSide.turnCheckerObject.GetComponent<Renderer>().enabled = false; // Disables the red debug sphere

			// For some reason the client BTR collider is disabled but the server collider is enabled.
			// Initially we assumed there was a reason for this so it was left as is.
			// Turns out disabling the server collider in favour of the client collider fixes the "BTR doing a wheelie" bug,
			// while preventing the player from walking through the BTR.
			// We also need to change the client collider's layer to HighPolyCollider due to unknown collisions that occur
			// when going down a steep slope.

			// Add collision debugger component to log collisions in the EFT Console
			Collider[] clientColliders = btrClientSide.GetComponentsInChildren<Collider>(true);
			//foreach (var collider in clientColliders)
			//{
			//    collider.gameObject.AddComponent<CollisionDebugger>();
			//}

			Collider[] serverColliders = btrServerSide.GetComponentsInChildren<Collider>(true);
			//foreach (var collider in serverColliders)
			//{
			//    collider.gameObject.AddComponent<CollisionDebugger>();
			//}

			Collider clientRootCollider = clientColliders.First(x => x.gameObject.name == "Root");

			// Retrieve all TerrainColliders
			List<TerrainCollider> terrainColliders = new();

			foreach (GPUInstancerManager manager in GPUInstancerManager.activeManagerList)
			{
				if (manager.GetType() != typeof(GPUInstancerDetailManager))
				{
					continue;
				}

				GPUInstancerDetailManager detailManager = (GPUInstancerDetailManager)manager;
				if (detailManager.terrain == null)
				{
					continue;
				}

				terrainColliders.Add(detailManager.terrain.GetComponent<TerrainCollider>());
			}

			// Make the Root collider ignore collisions with TerrainColliders
			foreach (TerrainCollider collider in terrainColliders)
			{
				Physics.IgnoreCollision(clientRootCollider, collider);
			}

			// Retrieve all wheel colliders on the serverside BTR
			const string wheelColliderParentName = "BTR_82_wheel";
			const string wheelColliderName = "Cylinder";

			Collider[] serverWheelColliders = serverColliders
				.Where(x => x.transform.parent.name.StartsWith(wheelColliderParentName) && x.gameObject.name.StartsWith(wheelColliderName))
				.ToArray();

			// Make the Root collider ignore collisions with the serverside BTR wheels
			foreach (Collider collider in serverWheelColliders)
			{
				Physics.IgnoreCollision(clientRootCollider, collider);
			}

			// Enable clientside BTR collider and disable serverside BTR collider
			const string exteriorColliderName = "BTR_82_exterior_COLLIDER";
			Collider serverExteriorCollider = serverColliders
				.First(x => x.gameObject.name == exteriorColliderName);
			Collider clientExteriorCollider = clientColliders
				.First(x => x.gameObject.name == exteriorColliderName);

			serverExteriorCollider.gameObject.SetActive(false);
			clientExteriorCollider.gameObject.SetActive(true);
			clientExteriorCollider.gameObject.layer = LayerMask.NameToLayer("HighPolyCollider");
		}

		public void ClientInteraction(Player player, PlayerInteractPacket packet)
		{
			BTRView btrView = gameWorld.BtrController.BtrView;
			if (btrView == null)
			{
				btrLogger.LogError("[SPT-BTR] BTRInteractionPatch - btrView is null");
				return;
			}

			if (player.IsYourPlayer)
			{
				btrView.Interaction(player, packet);
			}
			else
			{
				ObservedBTRInteraction(player, packet);
			}
			OnPlayerInteractDoor(player, packet);
		}

		private void ObservedBTRInteraction(Player player, PlayerInteractPacket packet)
		{
			BTRSide side = btrClientSide.method_9(packet.SideId);

			if (packet.InteractionType == EInteractionType.GoIn)
			{
				player.BtrState = EPlayerBtrState.Approach;
				btrClientSide.method_18(player);
				player.BtrState = EPlayerBtrState.GoIn;
				//side.AddPassenger(player, packet.SlotId);
				player.MovementContext.PlayerAnimator.SetBtrLayerEnabled(true);
				player.MovementContext.PlayerAnimator.SetBtrGoIn(packet.Fast);
				player.BtrState = EPlayerBtrState.Inside;
			}
			else if (packet.InteractionType == EInteractionType.GoOut)
			{
				player.BtrState = EPlayerBtrState.GoOut;
				player.MovementContext.PlayerAnimator.SetBtrGoOut(packet.Fast);
				player.MovementContext.PlayerAnimator.SetBtrLayerEnabled(false);
				(Vector3 start, Vector3 target) points = side.GoOutPoints();
				side.ApplyPlayerRotation(player.MovementContext, points.start, points.target + Vector3.up * 1.9f);
				player.BtrState = EPlayerBtrState.Outside;
				//side.RemovePassenger(player);
				btrClientSide.method_19(player);
			}
		}

		private void OnDestroy()
		{
			if (gameWorld == null)
			{
				return;
			}

			if (TraderServicesManager.Instance != null)
			{
				TraderServicesManager.Instance.OnTraderServicePurchased -= BtrTraderServicePurchased;
			}

			if (btrClientSide != null)
			{
				Debug.LogWarning("[SPT-BTR] BTRManager - Destroying btrClientSide");
				Destroy(btrClientSide.gameObject);
			}

			if (btrServerSide != null)
			{
				Debug.LogWarning("[SPT-BTR] BTRManager - Destroying btrServerSide");
				Destroy(btrServerSide.gameObject);
			}
		}
	}
}