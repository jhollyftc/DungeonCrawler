using UnityEngine;

public class RandomMeshSelector : MonoBehaviour
{
    [Tooltip("Assign the GameObjects that contain the MeshRenderers.")]
    public GameObject[] meshObjects;

    [Tooltip("Randomize every time this object is enabled.")]
    public bool randomizeOnEnable = false;

    private void Start()
    {
        RandomizeMesh();
    }

    private void OnEnable()
    {
        if (randomizeOnEnable)
            RandomizeMesh();
    }

    [ContextMenu("Randomize Mesh")]
    public void RandomizeMesh()
    {
        if (meshObjects == null || meshObjects.Length == 0)
            return;

        // Disable all
        foreach (GameObject obj in meshObjects)
        {
            if (obj != null)
                obj.GetComponent<MeshRenderer>().enabled = false;
        }

        // Pick one
        int index = Random.Range(0, meshObjects.Length);

        if (meshObjects[index] != null)
            meshObjects[index].GetComponent<MeshRenderer>().enabled = true;
    }
}