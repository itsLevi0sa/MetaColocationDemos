using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CustomTrailRenderer : MonoBehaviour
{
    [SerializeField] private float animationDuration;
    private LineRenderer lineRenderer;
    public Vector3 startPos;
    public Vector3 endPos;

    private void Start()
    {
        lineRenderer = GetComponent<LineRenderer>();
        HideLine();
    }

    public void StartLineAnimation()
    {
        lineRenderer.enabled = true;
        StartCoroutine(AnimateLine());
    }

    public void HideLine()
    {
        lineRenderer.enabled = false;
    }

    private IEnumerator AnimateLine()
    {
        float startTime = Time.time;

        startPos = lineRenderer.GetPosition(0);
        endPos = lineRenderer.GetPosition(1);

        Vector3 pos = startPos;
        while(pos != endPos)
        {
            float t = (Time.time - startTime) / animationDuration;
            pos = Vector3.Lerp(startPos, endPos, t);
            lineRenderer.SetPosition(1, pos);
            yield return null;
        }


    }
}
