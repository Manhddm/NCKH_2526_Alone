using Mirror;
using UnityEngine;
using UnityEngine.Rendering;
#if CINEMACHINE
using Cinemachine;
#endif

namespace Game.Core.Player
{
    // Ensures each client has its own camera following its local player.
    // On headless/server-only, does nothing.
    public class PlayerCamera : NetworkBehaviour
    {
#if CINEMACHINE
        [Header("Cinemachine")]
        [SerializeField] CinemachineVirtualCamera vcamPrefab;
        [SerializeField] bool useExistingChildIfFound = true;
        CinemachineVirtualCamera _instance;
#else
        [Header("Camera")]
        [SerializeField] GameObject cameraPrefab; // Fallback if Cinemachine not available
        GameObject _instance;
#endif

        public override void OnStartLocalPlayer()
        {
            base.OnStartLocalPlayer();

            // Headless/server-only guard
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;

#if CINEMACHINE
            if (useExistingChildIfFound)
            {
                _instance = GetComponentInChildren<CinemachineVirtualCamera>(true);
                if (_instance != null)
                {
                    _instance.Follow = transform;
                    _instance.LookAt = transform;
                    _instance.gameObject.SetActive(true);
                    return;
                }
            }

            if (vcamPrefab != null)
            {
                _instance = Instantiate(vcamPrefab, transform);
                _instance.Follow = transform;
                _instance.LookAt = transform;
            }
#else
            if (cameraPrefab != null)
            {
                _instance = Instantiate(cameraPrefab, transform);
            }
#endif
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
#if CINEMACHINE
            if (isLocalPlayer && _instance != null)
            {
                Destroy(_instance.gameObject);
                _instance = null;
            }
#else
            if (isLocalPlayer && _instance != null)
            {
                Destroy(_instance);
                _instance = null;
            }
#endif
        }
    }
}

