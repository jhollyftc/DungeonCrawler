using UnityEngine;

namespace DungeonGen
{
    /// <summary>Anything the player can use with the interact key.</summary>
    public interface IInteractable
    {
        string Prompt { get; }
        void Interact(Transform interactor);
    }
}