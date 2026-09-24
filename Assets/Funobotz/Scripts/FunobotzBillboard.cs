using UnityEngine;

/// <summary>
/// Yaw-only billboard for upright character cutouts. The pivot sits at the character's feet, so
/// the card turns in place on the floor. The mesh's visible face is -Z, so +Z points away from the viewer.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[AddComponentMenu("Funobotz/Funobotz Billboard")]
public sealed class FunobotzBillboard : MonoBehaviour
{
    [Tooltip("Camera to face. Empty = Camera.main.")]
    public Camera viewer;

    void LateUpdate()
    {
        var cam = viewer != null ? viewer : Camera.main;
        if (cam != null)
            Face(cam.transform.position);
    }

    public void Face(Vector3 viewerPosition)
    {
        var away = transform.position - viewerPosition;
        away.y = 0f;
        if (away.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.LookRotation(away, Vector3.up);
    }
}
