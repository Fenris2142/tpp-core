using System;
using System.Collections.Generic;
using System.Linq;
using TPP.Inputting.Inputs;

namespace TPP.Inputting
{
    public class MuteInputsToken
    {
        public bool Muted { get; set; }
    }

    /// <summary>
    /// Converts a <see cref="TimedInputSet"/> to a input map, which is a mapping from keys to any objects
    /// to be sent to a specific input frontend.
    /// This typically is turned into JSON to be read from a specific Lua script running in a specific emulator.
    /// </summary>
    public interface IInputMapper
    {
        /// Convert the timed input set to the respective input map representation.
        IDictionary<string, object> Map(TimedInputSet timedInputSet);
    }

    /// <summary>
    /// An input mapper that speaks the "default" input map dialect that is used for TwitchPlaysPokemon.
    /// Notable peculiarities:
    /// <ul>
    /// <li>Touches are transmitted via two fields "Touch_Screen_X" and "Touch_Screen_Y",
    ///     and drags additionally have "Touch_Screen_X2" and "Touch_Screen_Y2".</li>
    /// <li>Does not support multitouch (yet).</li>
    /// <li>The hold and sleep timings are submitted via "Held_Frames" and "Sleep_Frames".</li>
    /// <li>Regular buttons are represented as the button name in the input set being set to `true`
    ///     with proper casing, e.g. `{"A": true}` for `a` or `{"Left": true}` for `left`.</li>
    /// <li>Input map keys are Title-Cased (e.g. "Down"), as is expected by BizHawk,
    ///     but the returned input map's keys could be altered of course if necessary.
    ///     See <a href="http://tasvideos.org/LuaScripting/TableKeys.html">tasvideos.org/LuaScripting/TableKeys.html</a>
    ///     for a comprehensive list of possible casings.</li>
    /// <li>Unpressed buttons are omitted.</li>
    /// <li>If an button set is sided, input keys are prefixed with "P1 " for left and "P2 " for right.</li>
    /// </ul>
    /// </summary>
    public class DefaultTppInputMapper : IInputMapper
    {
        private static string ToLowerFirstUpper(string str) => str[..1].ToUpper() + str[1..].ToLower();

        private readonly float _fps;
        private readonly MuteInputsToken _muteInputsToken;

        /// <summary>
        /// TODO trigger toggles
        /// TODO eternal holds
        /// </summary>
        /// <param name="fps">Required for games that don't run at 60fps to correctly compute the frame timings.</param>
        /// <param name="muteInputsToken">the token indicating whether inputs should be muted,
        /// meaning the inputs should be consumed as usual but not actually perform any actions.</param>
        public DefaultTppInputMapper(float fps = 60, MuteInputsToken? muteInputsToken = null)
        {
            _fps = fps;
            _muteInputsToken = muteInputsToken ?? new MuteInputsToken { Muted = false };
        }

        public IDictionary<string, object> Map(TimedInputSet timedInputSet)
        {
            Dictionary<string, object> inputMap = new();
            bool isTouched = false;
            string buttonPrefix = "";
            foreach (var input in timedInputSet.InputSet.Inputs)
            {
                if (_muteInputsToken.Muted)
                {
                    // don't emit any actual inputs, just the meta fields like hold and sleep
                }
                else if (input is TouchscreenDragInput drag)
                {
                    if (isTouched) throw new ArgumentException("multitouch is not supported!");
                    isTouched = true;
                    inputMap["Touch_Screen_X"] = drag.X;
                    inputMap["Touch_Screen_Y"] = drag.Y;
                    inputMap["Touch_Screen_X2"] = drag.X2;
                    inputMap["Touch_Screen_Y2"] = drag.Y2;
                }
                else if (input is TouchscreenInput touch)
                {
                    if (isTouched) throw new ArgumentException("multitouch is not supported!");
                    isTouched = true;
                    inputMap["Touch_Screen_X"] = touch.X;
                    inputMap["Touch_Screen_Y"] = touch.Y;
                }
                else if (input is SideInput side)
                {
                    buttonPrefix = side.Side switch
                    {
                        InputSide.Left => "P1 ",
                        InputSide.Right => "P2 ",
                    };
                }
                else
                {
                    inputMap[ToLowerFirstUpper(input.ButtonName)] = true;
                }
            }
            if (!string.IsNullOrEmpty(buttonPrefix))
            {
                inputMap = inputMap.ToDictionary(kvp => buttonPrefix + kvp.Key, kvp => kvp.Value);
            }

            inputMap["Held_Frames"] = (int)Math.Round(timedInputSet.HoldDuration * _fps);
            inputMap["Sleep_Frames"] = (int)Math.Round(timedInputSet.SleepDuration * _fps);

            return inputMap;
        }
    }
}
