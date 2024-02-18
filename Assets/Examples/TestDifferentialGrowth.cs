using DiffentialGrowth;
using RosettaUI;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TestDifferentialGrowth : MonoBehaviour {

    [SerializeField]
    protected Links links = new();

    #region unity
    void Awake() {
        var root = links.root;
        var runner = links.runner;
        if (root != null) {
            root.Build(
                UI.Window(
                    UI.WindowLauncher(
                        "Runner",
                        UI.Window(
                            "Runner",
                            UI.Page(
                                UI.Button("Restart", () => runner.Restart()),
                                UI.Field(()=>runner.CuurTuner)
                                    .RegisterValueChangeCallback(()=>runner.Invalidate())
                            )
                        )
                    )
                )
            );
        }
    }
    #endregion

    #region declarations
    [System.Serializable]
    public class Links {
        public RosettaUIRoot root;
        public Runner runner;
    }
    #endregion
}