﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Groups;
using HarmonyLib;
using UnityEngine;

namespace TargetPortal
{
	public static class Map
	{
		public static bool Teleporting;
		private static readonly Dictionary<Minimap.PinData, ZDO> activePins = new();
		private static bool[]? _visibleIconTypes;

		[HarmonyPatch(typeof(TeleportWorldTrigger), nameof(TeleportWorldTrigger.OnTriggerEnter))]
		private class OpenMapOnPortalEnter
		{
			private static bool Prefix(TeleportWorldTrigger __instance, Collider collider)
			{
				if (collider.GetComponent<Player>() != Player.m_localPlayer)
				{
					return false;
				}

				Teleporting = true;
				Minimap.instance.ShowPointOnMap(__instance.transform.position);

				string? myId = PrivilegeManager.GetNetworkUserId();
				foreach (ZDO zdo in TargetPortal.knownPortals)
				{
					TargetPortal.PortalMode mode = (TargetPortal.PortalMode)zdo.GetInt("TargetPortal PortalMode");
					string ownerString = zdo.GetString("TargetPortal PortalOwnerId");
					if (TargetPortal.allowNonPublicPortals.Value == TargetPortal.Toggle.Off || mode == TargetPortal.PortalMode.Public || (mode == TargetPortal.PortalMode.Admin && TargetPortal.configSync.IsAdmin) || ownerString == myId.Replace("Steam_", "") || (mode == TargetPortal.PortalMode.Group && API.GroupPlayers().Contains(PlayerReference.fromPlayerInfo(ZNet.instance.m_players.FirstOrDefault(p => p.m_host == ownerString)))))
					{
						activePins.Add(Minimap.instance.AddPin(zdo.m_position, (Minimap.PinType)AddMinimapPortalIcon.pinType, zdo.GetString("tag"), false, false), zdo);
					}
				}

				if (_visibleIconTypes == null)
				{
					int l = Minimap.instance.m_visibleIconTypes.Length - 1;
					_visibleIconTypes = new bool[l];
					Array.Copy(Minimap.instance.m_visibleIconTypes, _visibleIconTypes, l);
					ToggleIconFilters();
				}

				return false;
			}
		}

		private static void ToggleIconFilters()
		{
			if (_visibleIconTypes == null) return;
			for (int i = 0, l = _visibleIconTypes.Length; i < l; i++)
				if(_visibleIconTypes[i]) Minimap.instance.ToggleIconFilter((Minimap.PinType)i);
		}

		public static void CancelTeleport()
		{
			Teleporting = false;

			foreach (Minimap.PinData pinData in activePins.Keys)
			{
				Minimap.instance.RemovePin(pinData);
			}
			activePins.Clear();
			
			ToggleIconFilters();
			_visibleIconTypes = null;
		}
		
		[HarmonyPatch(typeof(Minimap))]
		private class MinimapPatch
		{
			[HarmonyPrefix, HarmonyPatch(nameof(Minimap.UpdateMap))]
			private static bool UpdateMapPrefix()
			{
				if (!Teleporting) return true;

				if (ZInput.GetButtonDown("JoyButtonA"))
				{
					HandlePortalClick();
					return false;
				}

				if (ZInput.GetButtonDown("JoyDPadUp") || 
					ZInput.GetButtonDown("JoyDPadDown") || 
					ZInput.GetButtonDown("JoyDPadRight") ||
					ZInput.GetButtonDown("JoyTabRight") ||
					ZInput.GetButtonDown("JoyTabLeft")) return false;

				return true;
			}
		}

		public static Minimap.PinData? GetClosestPin()
		{
			Vector3 pos = ZInput.IsGamepadActive() 
				? new Vector3(Screen.width / 2f, Screen.height / 2f)
				: Input.mousePosition;
			pos = Minimap.instance.ScreenToWorldPoint(pos);

			Minimap mm = Minimap.instance;
			foreach (Minimap.PinData pinData in activePins.Keys) pinData.m_save = true;
			Minimap.PinData pin = mm.GetClosestPin(pos, mm.m_removeRadius * (mm.m_largeZoom * 2f));
			foreach (Minimap.PinData pinData in activePins.Keys) pinData.m_save = false;

			return pin;
		}

		private static void HandlePortalClick()
		{
			if (!Player.m_localPlayer.IsTeleportable())
			{
				Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_noteleport");
				return;
			}

			Minimap Minimap = Minimap.instance;
			Minimap.PinData? closestPin = GetClosestPin();

			if (closestPin is null)
			{
				return;
			}

			if (!activePins.TryGetValue(closestPin, out ZDO portalZDO))
			{
				return;
			}

			Quaternion rotation = portalZDO.GetRotation();

			Minimap.SetMapMode(Minimap.MapMode.Small);
			CancelTeleport();

			Player.m_localPlayer.TeleportTo(closestPin.m_pos + rotation * Vector3.forward + Vector3.up, rotation, true);
		}

		[HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
		public class LeavePortalModeOnMapClose
		{
			private static void Postfix(Minimap.MapMode mode)
			{
				if (mode != Minimap.MapMode.Large)
				{
					CancelTeleport();
				}
			}
		}

		[HarmonyPatch(typeof(Minimap), nameof(Minimap.Start))]
		public class AddMinimapPortalIcon
		{
			public static int pinType;

			private static void Postfix(Minimap __instance)
			{
				pinType = __instance.m_visibleIconTypes.Length;
				bool[] visibleIcons = new bool[pinType + 1];
				Array.Copy(__instance.m_visibleIconTypes, visibleIcons, pinType);
				__instance.m_visibleIconTypes = visibleIcons;

				__instance.m_icons.Add(new Minimap.SpriteData
				{
					m_name = (Minimap.PinType)pinType,
					m_icon = TargetPortal.portalIcon
				});
			}
		}

		[HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
		private class MapLeftClick
		{
			private static bool Prefix()
			{
				if (!Teleporting)
				{
					return true;
				}
				HandlePortalClick();
				return false;
			}
		}

		[HarmonyPatch]
		private class MapAlternativeClick
		{
			private static IEnumerable<MethodInfo> TargetMethods() => new[]
			{
				AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapDblClick)),
				AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapRightClick)),
				AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapMiddleClick))
			};

			private static bool Prefix()
			{
				return !Teleporting;
			}
		}
	}
}
