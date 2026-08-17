using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Anything a <see cref="Lever"/> can drive — a portcullis, a locked door, whatever comes
    /// next. One interface so the lever never learns which it is holding.
    ///
    /// IN ITS OWN FILE ON PURPOSE. It started inside Portcullis.cs, which made every consumer
    /// depend on that one file importing cleanly — and when it did not, three unrelated files
    /// reported "IGateLock could not be found" while the actual problem was somewhere else
    /// entirely. `IInteractable` living inside `DoorInteraction.cs` is the same latent trap.
    /// A type shared by several files belongs in a file named after itself.
    /// </summary>
    public interface IGateLock
    {
        /// <summary>Is the way through open right now?</summary>
        bool IsOpen { get; }

        /// <summary>
        /// Pulled. A toggling gate flips; a one-shot lock opens and stays open, so repeated
        /// pulls are harmless rather than a re-lock.
        /// </summary>
        void Toggle();

        /// <summary>
        /// Where the sound comes from — the GATE, never the lever.
        ///
        /// This is the whole reason levers are sited away from what they open: a clunk at your
        /// own hand carries no information, while a portcullis grinding up somewhere behind you
        /// tells you where to go.
        /// </summary>
        Transform SoundOrigin { get; }
    }
}
