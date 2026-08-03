using UnityEngine;

public class LightSaberAudioManager : MonoBehaviour
{
    [SerializeField] private AudioSource lightSaberTurnOn;
    [SerializeField] private AudioSource lightSaberTurnOff;
    
    [SerializeField] private AudioSource lightSaberMovement;
    [SerializeField] private AudioSource lightSaberSpark;

    public void PlayLightSaberStateSound(bool isOn)
    {
        if(!lightSaberTurnOn.isPlaying && isOn) lightSaberTurnOn.Play();
        if(!lightSaberTurnOff.isPlaying && !isOn) lightSaberTurnOff.Play();
    }

    public void PlayLightSaberMovementSound()
    {
        if(!lightSaberMovement.isPlaying) lightSaberMovement.Play();
    }
    
    public void PlayLightSaberSparkSound()
    {
        if(!lightSaberSpark.isPlaying) lightSaberSpark.Play();
    }
}
