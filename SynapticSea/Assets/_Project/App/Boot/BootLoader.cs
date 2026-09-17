using SynapticSea.Runtime.Session;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SynapticSea.App
{
    /// <summary>
    /// Boot scene entry (build index 0): composes the process-wide <see cref="AppServices"/> and loads the Title scene.
    /// The Godot project's main scene was <c>scenes/title_main.tscn</c>; Unity puts the service composition in front
    /// of it so every later scene finds the same services.
    /// </summary>
    public sealed class BootLoader : MonoBehaviour
    {
        [SerializeField] string nextScene = RunLaunchRequest.TitleSceneName;

        public string NextScene => nextScene;

        void Awake()
        {
            AppServices.Ensure();
        }

        void Start()
        {
            if (!Application.CanStreamedLevelBeLoaded(nextScene))
            {
                Debug.LogError("[Boot] scene not in the build: " + nextScene);
                return;
            }
            SceneManager.LoadScene(nextScene);
        }
    }
}
