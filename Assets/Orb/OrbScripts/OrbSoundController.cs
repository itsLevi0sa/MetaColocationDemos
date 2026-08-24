using UnityEngine;

public class OrbSoundController : MonoBehaviour
{
    public AudioClip[] audioClips;
    public KeyCode testKey = KeyCode.Space;
    private AudioSource audioSource;

    private void Start()
    {
        audioSource = GetComponent<AudioSource>();
    }

    private void Update()
    {
        if (Input.GetKeyDown(testKey))
        {
            PlayRandomClip();
        }
    }

    public void PlayRandomClip()
    {
        if (audioSource == null || audioClips == null || audioClips.Length == 0)
        {
            return;
        }

        audioSource.clip = audioClips[Random.Range(0, audioClips.Length)];
        audioSource.Play();
    }
}
