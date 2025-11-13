// UnityWebSocketClient.cs
// Multi-Drone Swarm RL Unity Client with proper thread handling
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;

public class UnityWebSocketClient : MonoBehaviour
{
    [Header("Server Configuration")]
    public string serverBase = "http://localhost:8000";
    public string wsBase = "ws://localhost:8000";

    [Header("Prefabs")]
    public GameObject TilePrefab;
    public List<GameObject> BuildingPrefabs;   // multiple building prefabs

    public GameObject BanditPrefab;
    public GameObject DronePrefab;
    public GameObject BlueFlagPrefab;
    public GameObject RedFlagPrefab;

    [Header("Grid Settings")]
    public float tileSize = 1.0f;
    public float droneHeight = 0.5f;

    [Header("Playback Control")]
    public float stepInterval = 0.5f; // seconds between steps for visualization
    public bool autoPlaySteps = true;

    [Header("Drone Colors")]
    public Color drone0Color = new Color(1f, 0.4f, 0.4f); // Red
    public Color drone1Color = new Color(0.4f, 1f, 0.4f); // Green
    public Color drone2Color = new Color(0.4f, 0.4f, 1f); // Blue

    [Header("UI (Optional)")]
public TextMeshProUGUI statsLeftText;
public TextMeshProUGUI statsRightText;


    // Grid data
    private int gridWidth, gridHeight, nDrones;
    private GameObject[,] tiles;
    private Dictionary<int, GameObject> droneObjects = new Dictionary<int, GameObject>();

    // WebSocket
    private ClientWebSocket ws;
    private CancellationTokenSource cts;
    private string currentEpisodeId;

    // Thread-safe queue for incoming WebSocket messages
    private ConcurrentQueue<string> incomingWsTextQueue = new ConcurrentQueue<string>();

    // Step processing queue (main thread only)
    private Queue<JObject> stepQueue = new Queue<JObject>();
    private bool isProcessingSteps = false;

    // Tracking visited cells and bandits to avoid duplicate flags
    private HashSet<Vector2Int> visitedPositions = new HashSet<Vector2Int>();
    private HashSet<Vector2Int> banditPositions = new HashSet<Vector2Int>();
    private HashSet<Vector2Int> redFlagPositions = new HashSet<Vector2Int>();

    // Map from grid cell -> spawned flag GameObject (blue or red)
    private Dictionary<Vector2Int, GameObject> flagObjects = new Dictionary<Vector2Int, GameObject>();

    // Episode statistics
    private int currentStep = 0;
    private float totalReward = 0;
    private float totalFuelConsumed = 0;
    private float coverage = 0;
    private int banditsFound = 0;
    private float fuelEfficiency = 0;

    // Per-drone stats
    private Dictionary<int, DroneStats> droneStats = new Dictionary<int, DroneStats>();

    private class DroneStats
    {
        public int id;
        public Vector2Int position;
        public string lastAction = "STAY";
        public float totalFuel = 0;
        public float lastReward = 0;
    }

    // --- UNITY LIFECYCLE ---

    async void Start()
    {
        Debug.Log("🚁 Starting Multi-Drone Swarm Unity Client...");

        // 1) Get environment initialization
        var initUrl = serverBase + "/init";
        var initJson = await GetJson(initUrl);
        if (initJson == null)
        {
            Debug.LogError("❌ Failed to get init data");
            return;
        }
        BuildGridFromInit(initJson);

        // 2) Start episode
        var startUrl = serverBase + "/quick_start"; // Using quick_start for convenience
        var startResp = await GetJson(startUrl);
        if (startResp == null || !startResp.ContainsKey("episode_id"))
        {
            Debug.LogError("❌ Failed to start episode");
            return;
        }

        currentEpisodeId = startResp["episode_id"].ToString();
        nDrones = Convert.ToInt32(startResp["n_drones"]);
        Debug.Log($"✅ Started episode {currentEpisodeId} with {nDrones} drones");

        // 3) Connect WebSocket
        string wsUrl = $"{wsBase}/ws/{currentEpisodeId}";
        await ConnectWebSocket(wsUrl);
    }

    void Update()
    {
        // Process incoming WebSocket messages on main thread
        while (incomingWsTextQueue.TryDequeue(out var txt))
        {
            try
            {
                var jobj = JsonConvert.DeserializeObject<JObject>(txt);
                HandleWsMessage_MainThread(jobj);
            }
            catch (Exception e)
            {
                Debug.LogError($"❌ Failed to parse WS JSON: {e.Message}");
            }
        }
    }

    private void OnDestroy()
    {
        Debug.Log("🔌 Cleaning up WebSocket...");
        try { cts?.Cancel(); } catch { }
        if (ws != null)
        {
            try { ws.Dispose(); } catch { }
            ws = null;
        }
    }

    private void OnApplicationQuit()
    {
        OnDestroy();
    }

    // --- NETWORK HELPERS ---

    async Task<Dictionary<string, object>> GetJson(string url)
    {
        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            var operation = www.SendWebRequest();
            while (!operation.isDone)
                await Task.Yield();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"❌ GET {url} failed: {www.error}");
                return null;
            }

            var txt = www.downloadHandler.text;
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, object>>(txt);
            }
            catch (Exception e)
            {
                Debug.LogError($"❌ JSON parse error: {e.Message}");
                return null;
            }
        }
    }

    async Task<Dictionary<string, object>> PostJson(string url, object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        using (UnityWebRequest www = UnityWebRequest.Put(url, bytes))
        {
            www.method = "POST";
            www.SetRequestHeader("Content-Type", "application/json");

            var operation = www.SendWebRequest();
            while (!operation.isDone)
                await Task.Yield();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"❌ POST {url} failed: {www.error}");
                return null;
            }

            var txt = www.downloadHandler.text;
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, object>>(txt);
            }
            catch (Exception e)
            {
                Debug.LogError($"❌ JSON parse error: {e.Message}");
                return null;
            }
        }
    }

    // --- GRID BUILDING ---

    void BuildGridFromInit(Dictionary<string, object> initJson)
    {
        try
        {
            gridWidth = Convert.ToInt32(initJson["grid_width"]);
            gridHeight = Convert.ToInt32(initJson["grid_height"]);
            nDrones = Convert.ToInt32(initJson["n_drones"]);

            Debug.Log($"📐 Building grid: {gridWidth}x{gridHeight} with {nDrones} drones");

            var cells = initJson["cells"] as JArray;
            tiles = new GameObject[gridWidth, gridHeight];

            // Build grid tiles
            for (int y = 0; y < gridHeight; y++)
            {
                var row = cells[y] as JArray;
                for (int x = 0; x < gridWidth; x++)
                {
                    string cellType = row[x].ToString();
                    Vector3 pos = new Vector3(x * tileSize, 0, -y * tileSize);

                    // Create floor tile
                    GameObject tile = Instantiate(TilePrefab, pos, Quaternion.identity, this.transform);
                    tiles[x, y] = tile;
                    tile.name = $"Tile_{x}_{y}";

                    // Place obstacles
                    if (cellType == "obstacle")
                    {
                        SpawnRandomBuilding(pos);
                    }

                    // Place bandits (surveillance targets)
                    else if (cellType == "bandit")
                    {
                        GameObject bandit = Instantiate(BanditPrefab, pos + Vector3.up * 0.1f,
                            Quaternion.identity, this.transform);
                        bandit.name = $"Bandit_{x}_{y}";
                        banditPositions.Add(new Vector2Int(x, y));
                    }
                }
            }

            // Spawn drones at initial positions
            var droneInitial = initJson["drone_initial"] as JArray;
            if (droneInitial != null)
            {
                for (int i = 0; i < droneInitial.Count; i++)
                {
                    var arr = droneInitial[i] as JArray;
                    int x = (int)arr[0];
                    int y = (int)arr[1];
                    SpawnDrone(i, x, y);
                }
            }

            // Handle pre-existing flags from server (if any)
            if (initJson.ContainsKey("blue_flags"))
            {
                var blueFlags = initJson["blue_flags"] as JArray;
                if (blueFlags != null)
                {
                    foreach (var f in blueFlags)
                    {
                        int x = (int)f[0];
                        int y = (int)f[1];
                        SpawnBlueFlag(x, y);
                    }
                }
            }

            if (initJson.ContainsKey("red_flags"))
            {
                var redFlags = initJson["red_flags"] as JArray;
                if (redFlags != null)
                {
                    foreach (var f in redFlags)
                    {
                        int x = (int)f[0];
                        int y = (int)f[1];
                        SpawnRedFlag(x, y);
                    }
                }
            }

            Debug.Log($"✅ Grid built successfully with {droneObjects.Count} drones");
        }
        catch (Exception e)
        {
            Debug.LogError($"❌ BuildGridFromInit failed: {e}");
        }
    }

    void SpawnDrone(int id, int x, int y)
    {
        Vector3 pos = new Vector3(x * tileSize, droneHeight, -y * tileSize);
        GameObject drone = Instantiate(DronePrefab, pos, Quaternion.identity, this.transform);
        drone.name = $"Drone_{id}";

        // Apply color based on drone ID
        Color droneColor = GetDroneColor(id);
        var renderer = drone.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = droneColor;
        }
        else
        {
            // Try child renderers
            var childRenderers = drone.GetComponentsInChildren<Renderer>();
            foreach (var r in childRenderers)
            {
                r.material.color = droneColor;
            }
        }

        droneObjects[id] = drone;

        // Initialize drone stats
        droneStats[id] = new DroneStats
        {
            id = id,
            position = new Vector2Int(x, y),
            lastAction = "STAY",
            totalFuel = 0,
            lastReward = 0
        };

        // Mark starting area as visited (sensory field)
        MarkVisitedArea(x, y);

        Debug.Log($"🚁 Spawned Drone {id} at ({x}, {y}) with color {droneColor}");
    }

    Color GetDroneColor(int id)
    {
        switch (id)
        {
            case 0: return drone0Color;
            case 1: return drone1Color;
            case 2: return drone2Color;
            default: return Color.white;
        }
    }

    // --- WEBSOCKET ---

    async Task ConnectWebSocket(string uri)
    {
        ws = new ClientWebSocket();
        cts = new CancellationTokenSource();
        try
        {
            await ws.ConnectAsync(new Uri(uri), cts.Token);
            Debug.Log($"✅ WebSocket connected to {uri}");
            _ = ReceiveLoop();
        }
        catch (Exception e)
        {
            Debug.LogError($"❌ WebSocket connection failed: {e}");
        }
    }

    async Task ReceiveLoop()
    {
        var buffer = new byte[16384]; // Larger buffer for multi-drone messages
        try
        {
            while (ws != null && ws.State == WebSocketState.Open)
            {
                var seg = new ArraySegment<byte>(buffer);
                WebSocketReceiveResult res = null;
                var data = new List<byte>();

                do
                {
                    res = await ws.ReceiveAsync(seg, cts.Token);
                    if (res.Count > 0)
                        data.AddRange(new ArraySegment<byte>(buffer, 0, res.Count));
                } while (!res.EndOfMessage);

                string txt = Encoding.UTF8.GetString(data.ToArray());
                // Enqueue for main thread processing
                incomingWsTextQueue.Enqueue(txt);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception e)
        {
            Debug.LogError($"❌ WebSocket receive error: {e}");
        }
    }

    // Called from Update() on main thread after parsing JSON
    void HandleWsMessage_MainThread(JObject msg)
    {
        try
        {
            var type = msg["type"]?.ToString();

            if (type == "init")
            {
                Debug.Log("📨 Received init message via WebSocket");
                // Grid already built from REST /init
            }
            else if (type == "step")
            {
                // Enqueue step for sequential playback
                stepQueue.Enqueue(msg);

                if (!isProcessingSteps && autoPlaySteps)
                {
                    StartCoroutine(ProcessStepQueue());
                }
            }
            else if (type == "done")
            {
                HandleDoneMessage(msg);
            }
            else
            {
                Debug.LogWarning($"⚠ Unknown message type: {type}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"❌ HandleWsMessage_MainThread error: {e}");
        }
    }

    // --- STEP PROCESSING ---

    IEnumerator ProcessStepQueue()
    {
        isProcessingSteps = true;

        while (stepQueue.Count > 0)
        {
            var msg = stepQueue.Dequeue();

            // Update step counter and coverage safely (check tokens exist)
            if (msg["step_index"] != null)
                currentStep = (int)msg["step_index"];
            if (msg["coverage"] != null)
                coverage = (float)msg["coverage"];

            float stepTotalReward = 0;
            float stepTotalFuel = 0;

            // Process each drone's movement
            var drones = msg["drones"] as JArray;
            if (drones != null)
            {
                foreach (var d in drones)
                {
                    int id = (int)d["id"];
                    var posArray = d["pos"] as JArray;
                    int nx = (int)posArray[0];
                    int ny = (int)posArray[1];
                    string action = d["action"]?.ToString() ?? "STAY";
                    float reward = d["reward"] != null ? (float)d["reward"] : 0f;
                    float fuelCost = d["fuel_cost"] != null ? (float)d["fuel_cost"] : 0f;

                    // Update drone stats
                    if (droneStats.ContainsKey(id))
                    {
                        droneStats[id].position = new Vector2Int(nx, ny);
                        droneStats[id].lastAction = action;
                        droneStats[id].totalFuel += fuelCost;
                        droneStats[id].lastReward = reward;
                    }

                    stepTotalReward += reward;
                    stepTotalFuel += fuelCost;

                    // Move drone smoothly
                    float moveDuration = Mathf.Max(0.05f, stepInterval * 0.7f);
                    MoveDroneTo(id, nx, ny, moveDuration);

                    // Mark visited area (3x3 sensory field)
                    MarkVisitedArea(nx, ny);
                }
            }

            totalReward += stepTotalReward;
            totalFuelConsumed += stepTotalFuel;

            // Process newly seen cells (if server explicitly sends them)
            var newlySeen = msg["newly_seen"] as JArray;
            if (newlySeen != null)
            {
                foreach (var c in newlySeen)
                {
                    var arr = c as JArray;
                    int x = (int)arr[0];
                    int y = (int)arr[1];
                    SpawnBlueFlag(x, y);
                }
            }

            // Process detected bandits
            var banditsFoundArray = msg["bandits_found"] as JArray;
            if (banditsFoundArray != null)
            {
                banditsFound = banditsFoundArray.Count;
                foreach (var b in banditsFoundArray)
                {
                    var arr = b as JArray;
                    int bx = (int)arr[0];
                    int by = (int)arr[1];
                    SpawnRedFlag(bx, by);
                }
            }

            // Update UI
            UpdateStatsUI();

            // Wait before processing next step
            yield return new WaitForSeconds(stepInterval);
        }

        isProcessingSteps = false;
    }

    void HandleDoneMessage(JObject msg)
{
    var summary = msg["summary"];
    if (summary == null) return;

    float finalReward = summary["reward"] != null ? (float)summary["reward"] : 0f;
    float finalCoverage = summary["coverage"] != null ? (float)summary["coverage"] : 0f;
    int finalBandits = summary["bandits"] != null ? (int)summary["bandits"] : 0;
    float finalFuel = summary["fuel_consumed"] != null ? (float)summary["fuel_consumed"] : 0f;
    float finalEfficiency = summary["fuel_efficiency"] != null ? (float)summary["fuel_efficiency"] : 0f;
    int uniqueBlocks = summary["unique_blocks"] != null ? (int)summary["unique_blocks"] : 0;

    // -----------------------------------
    // LEFT SIDE SUMMARY
    // -----------------------------------
    if (statsLeftText != null)
    {
        statsLeftText.color = Color.green;
        statsLeftText.text =
            $"🎉 EPISODE COMPLETE!\n\n" +
            $"Reward: {finalReward:F1}\n" +
            $"Coverage: {finalCoverage * 100:F1}%\n" +
            $"Bandits: {finalBandits}\n" +
            $"Fuel: {finalFuel:F1}\n" +
            $"Efficiency: {finalEfficiency:F3}\n" +
            $"Blocks: {uniqueBlocks}";
    }

    // -----------------------------------
    // RIGHT SIDE SUMMARY (Drone stats)
    // -----------------------------------
    if (statsRightText != null)
    {
        statsRightText.color = Color.green;

        StringBuilder sb = new StringBuilder();

        foreach (var stat in droneStats.Values)
        {
            sb.AppendLine(
                $"D{stat.id}: {stat.lastAction} | " +
                $"F:{stat.totalFuel:F1} | " +
                $"R:{stat.lastReward:F1}"
            );
        }

        statsRightText.text = sb.ToString();
    }

    Debug.Log("=== EPISODE SUMMARY ===");
}


    // --- VISITED AREA & FLAGS ---

    void MarkVisitedArea(int centerX, int centerY)
    {
        // 3x3 sensory field around drone (matches Python sensory_field logic)
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                int nx = centerX + dx;
                int ny = centerY + dy;

                // Bounds check
                if (nx < 0 || ny < 0 || nx >= gridWidth || ny >= gridHeight)
                    continue;

                Vector2Int pos = new Vector2Int(nx, ny);

                // If bandit known on this tile, spawn red (will avoid duplicates)
                if (banditPositions.Contains(pos))
                {
                    SpawnRedFlag(nx, ny);
                }
                else
                {
                    SpawnBlueFlag(nx, ny);
                }
            }
        }
    }

    void SpawnBlueFlag(int x, int y)
    {
        Vector2Int pos = new Vector2Int(x, y);

        // If a red flag already exists here, do nothing (red overrides blue)
        if (redFlagPositions.Contains(pos))
            return;

        // If any flag object exists and it's already a blue flag, skip
        if (flagObjects.TryGetValue(pos, out var existing))
        {
            if (existing != null && existing.name.StartsWith("BlueFlag"))
                return;

            // if existing is a red flag (shouldn't happen because we checked redFlagPositions),
            // we skip leaving the red as-is.
            if (existing != null && existing.name.StartsWith("RedFlag"))
                return;

            // else destroy unexpected and continue to spawn blue
            Destroy(existing);
            flagObjects.Remove(pos);
        }

        // Instantiate blue flag and record
        Vector3 worldPos = new Vector3(x * tileSize, 0.15f, -y * tileSize);
        GameObject flag = Instantiate(BlueFlagPrefab, worldPos, Quaternion.identity, this.transform);
        flag.name = $"BlueFlag_{x}_{y}";

        flagObjects[pos] = flag;
        visitedPositions.Add(pos);
    }
    // Spawn a random building prefab instead of a single obstacle
    void SpawnRandomBuilding(Vector3 position)
    {
        if (BuildingPrefabs == null || BuildingPrefabs.Count == 0)
        {
            Debug.LogWarning("⚠ No building prefabs assigned in Inspector!");
            return;
        }

        int index = UnityEngine.Random.Range(0, BuildingPrefabs.Count);
        GameObject prefab = BuildingPrefabs[index];

        GameObject building = Instantiate(
            prefab,
            position + Vector3.up * 0.1f,       // slight lift so it doesn't clip
            Quaternion.identity,
            this.transform
        );

        building.name = $"Building_{index}_{position.x}_{position.z}";
    }


    void SpawnRedFlag(int x, int y)
    {
        Vector2Int pos = new Vector2Int(x, y);

        // If red already exists, nothing to do
        if (redFlagPositions.Contains(pos))
            return;

        // If a blue exists, remove it and replace with red
        if (flagObjects.TryGetValue(pos, out var existing))
        {
            if (existing != null && existing.name.StartsWith("BlueFlag"))
            {
                Destroy(existing);
                flagObjects.Remove(pos);
            }
            // if existing is a RedFlag but redFlagPositions wasn't set, we still set it below
        }

        Vector3 worldPos = new Vector3(x * tileSize, 0.25f, -y * tileSize);
        GameObject flag = Instantiate(RedFlagPrefab, worldPos, Quaternion.identity, this.transform);
        flag.name = $"RedFlag_{x}_{y}";

        flagObjects[pos] = flag;
        visitedPositions.Add(pos);
        redFlagPositions.Add(pos);
        banditPositions.Add(pos);

        Debug.Log($"Spawned RED flag at {x},{y}");
    }

    // --- DRONE MOVEMENT ---

    void MoveDroneTo(int id, int x, int y, float duration = 0.3f)
    {
        if (!droneObjects.ContainsKey(id))
        {
            Debug.LogWarning($"⚠ MoveDroneTo: Unknown drone ID {id}");
            return;
        }

        Vector3 target = new Vector3(x * tileSize, droneHeight, -y * tileSize);
        StartCoroutine(LerpMove(droneObjects[id], target, duration));
    }

    IEnumerator LerpMove(GameObject go, Vector3 target, float duration)
    {
        if (go == null)
            yield break;

        Vector3 start = go.transform.position;
        float t = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            go.transform.position = Vector3.Lerp(start, target, Mathf.Clamp01(t / duration));
            yield return null;
        }

        go.transform.position = target;
    }

    // --- UI UPDATE ---

    void UpdateStatsUI()
{
    if (statsLeftText == null || statsRightText == null) return;

    fuelEfficiency = totalFuelConsumed > 0 ?
        (visitedPositions.Count / totalFuelConsumed) : 0;

    // --------------------
    // LEFT SIDE (Episode info)
    // --------------------
    StringBuilder left = new StringBuilder();
    left.AppendLine($"Bandits: {banditsFound}");
    left.AppendLine($"Fuel: {totalFuelConsumed:F1}");
    left.AppendLine($"Efficiency: {fuelEfficiency:F3}");

    statsLeftText.text = left.ToString();


    // --------------------
    // RIGHT SIDE (Per-drone info)
    // --------------------
    StringBuilder right = new StringBuilder();

    foreach (var stat in droneStats.Values)
    {
        right.AppendLine(
            $"D{stat.id}: {stat.lastAction}  | " +
            $"F:{stat.totalFuel:F1} | " +
            $"R:{stat.lastReward:F1}"
        );
    }

    statsRightText.text = right.ToString();
}


}
