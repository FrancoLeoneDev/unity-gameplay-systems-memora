using System;
using System.Collections.Generic;

[Serializable]
public class SaveData
{
    public int version = 1;
    public SaveMetadata metadata = new SaveMetadata();
    public int sceneBuildIndex;

    // Identidad (GUID de PhotoData) del recuerdo en el que estabas al guardar; null = estabas en la casa.
    // Lo escribe SceneControllerManager en OnBeforeSave. Permite que el Continue re-monte el recuerdo
    // ADITIVO sobre MainHouseTesis (rama de resume) en vez de cargar el recuerdo solo (partida corrupta).
    // Retro-compat: un save viejo sin este campo deserializa a null -> carga normal en la casa.
    public string recuerdoActivoId = null;
    public Dictionary<string, Dictionary<string, object>> entityStates
        = new Dictionary<string, Dictionary<string, object>>();

    // Canal de estado global: para managers/singletons que no son entidades de
    // escena con GUID (DreadSystem, SubtleChangeManager, EventsOnBackManager, etc.).
    // Indexado por una clave estable definida por cada manager.
    public Dictionary<string, object> globalState
        = new Dictionary<string, object>();
}

[Serializable]
public class SaveMetadata
{
    public string timestamp = "";
    public float playTimeSeconds;
    public string areaName = "";
    public string sceneName = "";
}
