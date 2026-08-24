using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LightFlicker : MonoBehaviour
{
    public Light targetLight;
    public float flickerIntensity = 2f;
    public float flickersPerSecond = 4.0f;
    public float speedRandomnsess = 5.0f;
    public float mappedIntensity;
    public Material emissiveMat;

    private float time;
    private float startingIntensity;

    private void Start()
    {
        startingIntensity = targetLight.intensity;
        emissiveMat = GetComponent<MeshRenderer>().material;

    }

    float Remap(float x, float xMin, float xMax, float yMin, float yMax)
    {
        // First, normalize x within the [0, 1] range based on its position in the x range
        float normalizedX = Mathf.InverseLerp(xMin, xMax, x);

        // Then, use that normalized value to interpolate within the y range
        float remappedValue = Mathf.Lerp(yMin, yMax, normalizedX);

        return remappedValue;
    }

    private void Update()
    {
        time += Time.deltaTime * (1 - Random.Range(-speedRandomnsess, speedRandomnsess)) * Mathf.PI;
        targetLight.intensity = startingIntensity + Mathf.Sin(time * flickersPerSecond) * flickerIntensity;
        mappedIntensity = Remap(mappedIntensity, -5, 1, 0, targetLight.intensity);
        emissiveMat.SetColor("_EmissionColor", Color.white * mappedIntensity);

    }
}
