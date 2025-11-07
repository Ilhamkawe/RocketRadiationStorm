using System;
using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace RocketRadiationStorm.Commands
{
    public class RadiationCommand : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;

        public string Name => "radiation";

        public string Help => "Controls the radiation storm";

        public string Syntax => "/radiation <start|stop|status>";

        public List<string> Aliases => new List<string> { "radstorm" };

        public List<string> Permissions => new List<string> { "radiationstorm.manage" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (command.Length == 0)
            {
                SendMessage(caller, Syntax);
                return;
            }

            var sub = command[0].ToLowerInvariant();

            try
            {
                switch (sub)
                {
                    case "start":
                        RadiationStormPlugin.Instance.StartStorm();
                        SendMessage(caller, RadiationStormPlugin.Instance.Translate("storm_start"));
                        break;
                    case "stop":
                        RadiationStormPlugin.Instance.StopStorm();
                        SendMessage(caller, RadiationStormPlugin.Instance.Translate("storm_stop"));
                        break;
                    case "status":
                        var state = RadiationStormPlugin.Instance.StormActive ? "active" : "inactive";
                        SendMessage(caller, RadiationStormPlugin.Instance.Translate("storm_status", state));
                        break;
                    default:
                        SendMessage(caller, Syntax);
                        break;
                }
            }
            catch (InvalidOperationException ex)
            {
                SendMessage(caller, ex.Message);
            }
        }

        private static void SendMessage(IRocketPlayer caller, string message)
        {
            UnturnedChat.Say(caller, message, Color.green);
        }
    }
}

