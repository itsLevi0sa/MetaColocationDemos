using UnityEngine;

namespace MetaColocationDemos.Spectator
{
    /// <summary>
    /// Simple local, non-networked free-fly camera for a PC spectator watching a colocated session:
    /// WASD/QE to move, hold right mouse button to look around.
    /// </summary>
    public class SpectatorFlyCamera : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 3f;
        [SerializeField] private float lookSensitivity = 2f;

        private float _yaw;
        private float _pitch;

        private void Start()
        {
            var angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;
        }

        private void Update()
        {
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * lookSensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * lookSensitivity;
                transform.eulerAngles = new Vector3(_pitch, _yaw, 0);
            }

            var move = new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxis("Vertical"));
            if (Input.GetKey(KeyCode.E)) move.y += 1;
            if (Input.GetKey(KeyCode.Q)) move.y -= 1;

            transform.Translate(move * (moveSpeed * Time.deltaTime), Space.Self);
        }
    }
}
