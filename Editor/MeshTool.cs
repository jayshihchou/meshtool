using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class MeshTool : EditorWindow
{
    [MenuItem("Custom/Mesh Tool")]
    static void Open()
    {
        GetWindow<MeshTool>("Mesh Tool");
    }

    string ProjectName
    {
        get
        {
            var list = Application.dataPath.Split('/');
            return list[^2];
        }
    }

    private void OnEnable()
    {
        var projectName = ProjectName;
        if (EditorPrefs.HasKey(projectName + ".EditMeshView.output"))
            output = EditorPrefs.GetString(projectName + ".EditMeshView.output");
        if (EditorPrefs.HasKey(projectName + ".EditMeshView.vertexSize"))
            vertexSize = EditorPrefs.GetFloat(projectName + ".EditMeshView.vertexSize");
        if (EditorPrefs.HasKey(projectName + ".EditMeshView.normalSize"))
            normalSize = EditorPrefs.GetFloat(projectName + ".EditMeshView.normalSize");
        if (EditorPrefs.HasKey(projectName + ".EditMeshView.selectedIndex"))
            selectedIndex = new HashSet<int>(JsonUtility.FromJson<SelectedIndicesData>(EditorPrefs.GetString(projectName + ".EditMeshView.selectedIndex")).data);
        else selectedIndex = new HashSet<int>();

        if (EditorPrefs.HasKey(projectName + ".EditMeshView.savedSelections"))
            savedSelections = JsonUtility.FromJson<SelectedIndicesCollection>(EditorPrefs.GetString(projectName + ".EditMeshView.savedSelections")).datas;

#if UNITY_2019_1_OR_NEWER
        SceneView.duringSceneGui -= OnScene;
        SceneView.duringSceneGui += OnScene;
#else
		SceneView.onSceneGUIDelegate -= OnScene;
		SceneView.onSceneGUIDelegate += OnScene;
#endif
        wantsMouseMove = true;
        autoRepaintOnSceneChange = true;
        wantsMouseEnterLeaveWindow = true;

        minSize = new Vector2(500f, 350f);

        OnSelectionChange();
    }

    private void OnDisable()
    {
        var projectName = ProjectName;
        EditorPrefs.SetString(projectName + ".EditMeshView.output", output);
        EditorPrefs.SetString(projectName + ".EditMeshView.selectedIndex", JsonUtility.ToJson(new SelectedIndicesData() { data = selectedIndex.ToArray() }));
        EditorPrefs.SetString(projectName + ".EditMeshView.savedSelections", JsonUtility.ToJson(new SelectedIndicesCollection() { datas = savedSelections }));
        EditorPrefs.SetFloat(projectName + ".EditMeshView.vertexSize", vertexSize);
        EditorPrefs.SetFloat(projectName + ".EditMeshView.normalSize", normalSize);
        Reset();
#if UNITY_2019_1_OR_NEWER
        SceneView.duringSceneGui -= OnScene;
#else
		SceneView.onSceneGUIDelegate -= OnScene;
#endif
    }

    MeshFilter nextMf;
    SkinnedMeshRenderer nextSmr;
    MeshFilter mf;
    SkinnedMeshRenderer smr;
    Mesh mesh;
    Mesh editMesh;
    Mesh nextMesh;

    private void OnSelectionChange()
    {
        var go = Selection.activeGameObject;
        if (!go || !go.activeInHierarchy) return;
        MeshFilter meshFilter = go.GetComponent<MeshFilter>();
        if (meshFilter)
        {
            nextMesh = meshFilter.sharedMesh;
            nextTransform = meshFilter.transform;
            nextMf = meshFilter;
        }
        else
        {
            SkinnedMeshRenderer skinnedMeshRenderer = go.GetComponent<SkinnedMeshRenderer>();
            if (skinnedMeshRenderer)
            {
                nextMesh = new Mesh();
                //skinnedMeshRenderer.BakeMesh(nextMesh);
                nextMesh = skinnedMeshRenderer.sharedMesh;
                nextTransform = skinnedMeshRenderer.transform;
                nextSmr = skinnedMeshRenderer;
            }
        }
        if (mesh == nextMesh || editMesh == nextMesh)
        {
            nextMesh = null;
            transform = nextTransform;
        }
    }

    int lastRebuildFrame;
    bool DrawPoint(int index, bool selected)
    {
        if (multiPointsViewList == null && pointViewSelectedIndex == index)
        {
            pointViewSelectedIndex = -1;
            return true;
        }
        var vertex = vertices[index];
        var v = transform.TransformPoint(vertex);

        if (drawNormal)
        {
            var n = normals[index];
            //n = transform.TransformVector(n).normalized;
            if (drawNormalColor)
                Handles.color = new Color(n.x, n.y, n.z, 1f);
            else
                Handles.color = Color.cyan;
            Handles.DrawLine(v, v + transform.TransformVector(n).normalized * normalSize);
        }

        // if (subindexMode)
        // {
        //     if (selected)
        //         Handles.color = Color.red;
        //     else
        //         Handles.color = Color.yellow;
        // }
        // else
        // {
        if (selected)
            Handles.color = Color.yellow;
        else
            Handles.color = Color.green;
        // }

        bool res = false;
        if (!hideVertex)
        {
            res = Handles.Button(v, Quaternion.identity, vertexSize, vertexSize, Handles.DotHandleCap);
        }

        if (selected)
        {
            if (!hideSelectedIndex)
                Handles.Label(v, index.ToString(), labelStyle);
        }
        else
        {
            if (drawIndex)
                Handles.Label(v, index.ToString(), labelStyle);
        }

        if (res && !selected)
        {
            var sameIndices = vertexConnection.GetSameIndices(vertex);

            if (sameIndices == null)
            {
                if (lastRebuildFrame != sceneFrameCount)
                {
                    lastRebuildFrame = sceneFrameCount;
                    RebuildConnection();
                }
            }
            sameIndices = vertexConnection.GetSameIndices(vertex);
            if (sameIndices == null)
            {
                Debug.LogError($"sameIndices == null: {sameIndices == null}?");
            }
            else if (sameIndices.Count > 1)
            {
                multiPointsViewList = sameIndices;
                res = false;
            }
        }

        // if (hideNotSelected)
        // {
        //     if (selected && !hideSelectedIndex)
        //         Handles.Label(v, index.ToString(), labelStyle);
        // }
        // else if (drawIndex || selected)
        // {
        //     if(!hideSelectedIndex)
        //         Handles.Label(v, index.ToString(), labelStyle);
        // }

        return res;
    }

    GUIStyle labelStyle;
    float vertexSize = 0.01f;
    float normalSize = 0.01f;
    readonly HashSet<int> sceneViewIndices = new();

    void CheckPointInView(SceneView scene)
    {
        var cam = scene.camera;
        var camDir = cam.transform.forward;
        Bounds box = new();
        box.SetMinMax(new(0, 0, 0), new(1, 1, cam.farClipPlane));
        sceneViewIndices.Clear();
        for (int i = vertices.Length - 1; i >= 0; --i)
        {
            var vertex = vertices[i];
            var v = transform.TransformPoint(vertex);
            var viewport = cam.WorldToViewportPoint(v);

            if (box.Contains(viewport))
            {
                if (drawFrontOnly)
                {
                    var n = normals[i];
                    n = transform.TransformDirection(n);
                    if (Vector3.Dot(n, camDir) < 0f)
                        sceneViewIndices.Add(i);
                }
                else
                    sceneViewIndices.Add(i);
            }
        }
        // Debug.Log($"draw count: {sceneViewIndices.Count}");
    }

    Vector3[] moveAllStarts;
    Vector3 moveAllVertex;
    readonly int moveAllIndex;
    Vector3 moveAllDir;
    Vector3 moveAllCentre;

    void MoveAllStart()
    {
        if (moveAllStarts == null || moveAllStarts.Length != vertices.Length)
        {
            moveAllStarts = new Vector3[vertices.Length];
        }
        moveAllDir = Vector3.zero;
        moveAllCentre = Vector3.zero;
        foreach (var i in selectedIndex)
        {
            moveAllStarts[i] = vertices[i];
            moveAllCentre += vertices[i];
        }
        moveAllCentre /= selectedIndex.Count;
    }

    void MoveAllUpdate()
    {
        foreach (var i in selectedIndex)
        {
            vertices[i] = moveAllStarts[i] + moveAllDir;
        }
    }
    public static Vector3 AngleToVecXY(float angle)
    {
        angle *= Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
    }

    public static float GetAngleXY(Vector3 vec)
    {
        return Mathf.Atan2(vec.y, vec.x) * Mathf.Rad2Deg;
    }

    Vector3[] rotateAllStarts;
    Vector3 rotateAllCentre;
    Quaternion rotateAllQuat;
    int rotateAllIndex;
    Vector3 rotateAllVertex;
    Vector3 rotateAllDir;
    float rotateAngle;
    void RotateAllStart()
    {
        if (rotateAllStarts == null || rotateAllStarts.Length != vertices.Length)
        {
            rotateAllStarts = new Vector3[vertices.Length];
        }
        rotateAngle = 0f;
        rotateAllQuat = Quaternion.identity;
        rotateAllDir = Vector3.zero;
        rotateAllCentre = Vector3.zero;
        foreach (var i in selectedIndex)
        {
            var v = vertices[i];
            rotateAllStarts[i] = v;
            rotateAllCentre += v;
        }
        rotateAllCentre /= selectedIndex.Count;
    }

    void RotateUpdate()
    {
        var e = rotateAllQuat.eulerAngles;
        rotateAngle = e.z;
        // rotateAngle += rotateAllDir.x;
        foreach (var i in selectedIndex)
        {
            var dir = rotateAllStarts[i] - rotateAllCentre;
            var angle = GetAngleXY(dir.normalized);
            angle += rotateAngle;
            vertices[i] = rotateAllCentre + AngleToVecXY(angle).normalized * dir.magnitude;
        }
    }

    Vector3 unfoldCentre;
    float unfoldDistance;
    Vector3[] unfoldDirs;
    Vector3[] unfoldStarts;
    // Vector3[] unfoldSts;
    float[] unfoldLengths;
    int unfoldMovingIndex;
    Vector3 unfoldSelectStart;
    void UnfoldStart(Vector3[] verts)
    {
        unfoldCentre = Vector3.zero;
        float maxz = float.MinValue;
        foreach (var i in selectedIndex)
        {
            var v = verts[i];
            unfoldCentre += v;
            if (v.z > maxz)
            {
                maxz = v.z;
            }
        }
        unfoldCentre /= selectedIndex.Count;
        unfoldCentre.z = maxz;
        unfoldDistance = 0f;

        if (unfoldLengths == null || unfoldLengths.Length != verts.Length)
        {
            unfoldLengths = new float[verts.Length];
        }
        if (unfoldDirs == null || unfoldDirs.Length != verts.Length)
        {
            unfoldDirs = new Vector3[verts.Length];
        }
        if (unfoldStarts == null || unfoldStarts.Length != verts.Length)
        {
            unfoldStarts = new Vector3[verts.Length];
        }
        // if (unfoldSts == null || unfoldSts.Length != verts.Length)
        // {
        //     unfoldSts = new Vector3[verts.Length];
        // }

        float unfoldMaxDistance = float.MinValue;

        foreach (var i in selectedIndex)
        {
            unfoldStarts[i] = vertices[i];

            var v = verts[i];
            // unfoldSts[i] = v;
            var dir = unfoldCentre - v;
            unfoldDirs[i] = dir;
            var dist = dir.magnitude;
            unfoldLengths[i] = dist;
            if (dist > unfoldMaxDistance)
            {
                unfoldMaxDistance = dist;
            }
        }
        unfoldMaxDistance = 1f / unfoldMaxDistance;
        foreach (var i in selectedIndex)
        {
            unfoldLengths[i] *= unfoldMaxDistance;
        }
    }

    void UnfoldUpdate()
    {
        foreach (var i in selectedIndex)
        {
            var dir = unfoldDirs[i];
            float dirDist = unfoldLengths[i];
            dir.z = 0f;
            vertices[i] = unfoldStarts[i] + dir.normalized * Mathf.Clamp(dirDist * unfoldDistance, 0f, unfoldDistance);
        }
    }

    // void UnfoldGizmos()
    // {
    //     foreach (var i in selectedIndex)
    //     {
    //         Handles.color = Color.green;
    //         Handles.DrawWireCube(unfoldSts[i], vertexSize * Vector3.one);
    //         Handles.color = Color.yellow;
    //         Handles.DrawLine(unfoldSts[i], unfoldSts[i] + unfoldDirs[i]);
    //     }
    //     Handles.color = Color.red;
    //     Handles.DrawWireCube(unfoldCentre, vertexSize * Vector3.one);
    //     Handles.color = Color.white;
    // }

    List<int> multiPointsViewList = null;
    int pointViewSelectedIndex = -1;
    int lastSelectInPointview = -1;
    void SelectMultiPointsInSamePositionView()
    {
        if (multiPointsViewList == null || multiPointsViewList.Count < 1) return;
        var oldHandleColor = Handles.color;
        Handles.color = Color.magenta;
        var pos = transform.TransformPoint(vertices[multiPointsViewList[0]]);
        Handles.Button(pos, Quaternion.identity, vertexSize, vertexSize, Handles.DotHandleCap);
        Handles.BeginGUI();
        {
            var oldGUIColor = GUI.color;
            GUI.color = Color.red;
            GUILayout.BeginHorizontal(GUI.skin.box);
            {
                GUILayout.Space(100);
                GUILayout.BeginVertical();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Same Indices");
                if (GUILayout.Button("Focus"))
                {
                    SceneView.lastActiveSceneView.Frame(new Bounds(pos, 100f * vertexSize * Vector3.one), false);
                }
                GUILayout.EndHorizontal();

                for (int i = 0, imax = multiPointsViewList.Count; i < imax; ++i)
                {
                    if (GUILayout.Button($"Vertex: {multiPointsViewList[i]}"))
                    {
                        lastSelectInPointview = multiPointsViewList[i];
                        GetConnection(lastSelectInPointview);
                    }
                }

                GUILayout.BeginHorizontal();
                if (lastSelectInPointview != -1 && GUILayout.Button($"Select Vertex: {lastSelectInPointview}"))
                {
                    pointViewSelectedIndex = lastSelectInPointview;
                    lastSelectInPointview = -1;
                    multiPointsViewList = null;
                    ClearConnection();
                }
                if (GUILayout.Button("Back"))
                {
                    pointViewSelectedIndex = -1;
                    lastSelectInPointview = -1;
                    multiPointsViewList = null;
                    ClearConnection();
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUI.color = oldGUIColor;
        }
        Handles.EndGUI();
        Handles.color = oldHandleColor;
    }

    int sceneFrameCount;
    void OnScene(SceneView scene)
    {
        ++sceneFrameCount;
        if (hide) return;
        if (editMesh != null && transform != null)
        {
            if (labelStyle == null) labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.normal.textColor = Color.red;

            CheckPointInView(scene);

            if (editMode)
            {
                bool vertsDirty = false;
                Handles.color = Color.cyan;
                if (hideNotSelected)
                {
                    Vector3? moveAll = null;
                    Vector3 moveAllDiff = Vector3.zero;
                    int movingIndex = -1;
                    foreach (var i in selectedIndex)
                    {
                        if (!sceneViewIndices.Contains(i)) continue;
                        var vertex = vertices[i];
                        var v = transform.TransformPoint(vertex);
                        v = Handles.FreeMoveHandle(v, Quaternion.identity, vertexSize, Vector3.zero, Handles.DotHandleCap);
                        if (GUI.changed)
                        {
                            v = transform.InverseTransformPoint(v);
                            if (findSameIndex)
                            {
                                var sameIndices = vertexConnection.GetSameIndices(vertex);
                                if (sameIndices == null)
                                {
                                    needToRebuildConnection = true;
                                }
                                else
                                {
                                    for (int j = sameIndices.Count - 1; j >= 0; --j)
                                    {
                                        vertices[sameIndices[j]] = v;
                                    }
                                    editMesh.vertices = vertices;
                                    editMesh.RecalculateNormals();
                                }
                            }
                            else
                            {
                                movingIndex = i;
                                moveAllDiff = v - vertices[i];
                                moveAll = vertices[i] = v;
                                vertsDirty = true;
                                break;
                            }
                        }
                    }
                    if (moveAll.HasValue)
                    {
                        if (moveAllSelect)
                        {
                            foreach (var i in selectedIndex)
                            {
                                vertices[i] = moveAll.Value;
                            }
                            vertsDirty = true;
                        }
                        else if (unfoldMode)
                        {
                            if (movingIndex != unfoldMovingIndex)
                            {
                                unfoldMovingIndex = movingIndex;
                                unfoldDistance = 0f;
                                unfoldSelectStart = vertices[movingIndex];
                            }
                            unfoldDistance = (vertices[movingIndex] - unfoldSelectStart).magnitude;
                            UnfoldUpdate();
                            vertsDirty = true;
                        }
                        else if (rotateAllSelect)
                        {
                            if (movingIndex != rotateAllIndex)
                            {
                                rotateAllIndex = movingIndex;
                                rotateAngle = 0f;
                                rotateAllVertex = vertices[movingIndex];
                            }
                            rotateAllDir = rotateAllVertex - vertices[rotateAllIndex];
                            rotateAllDir = scene.camera.worldToCameraMatrix.MultiplyVector(rotateAllDir);
                            rotateAllDir.y = -rotateAllDir.y;
                            RotateUpdate();
                            vertsDirty = true;
                        }
                    }
                    if (moveAllSelectDiffMode)
                    {
                        var old = moveAllCentre + moveAllDir;
                        var p = Handles.PositionHandle(old, Quaternion.identity);
                        if (old != p)
                        {
                            moveAllDir = moveAllCentre - p;
                            moveAllDir = scene.camera.worldToCameraMatrix.MultiplyVector(moveAllDir);
                            moveAllDir.y = -moveAllDir.y;
                            MoveAllUpdate();
                            vertsDirty = true;
                        }
                    }
                    if (rotateAllSelect)
                    {
                        var quat = Handles.RotationHandle(rotateAllQuat, rotateAllCentre);
                        if (quat != rotateAllQuat)
                        {
                            rotateAllQuat = quat;
                            var e = rotateAllQuat.eulerAngles;
                            e.x = e.y = 0f;
                            rotateAllQuat.eulerAngles = e;
                            RotateUpdate();
                            vertsDirty = true;
                        }
                    }
                    if (vertsDirty)
                    {
                        editMesh.vertices = vertices;
                        editMesh.RecalculateNormals();
                    }
                }
                else
                {
                    for (int i = vertices.Length - 1; i >= 0; --i)
                    {
                        if (!sceneViewIndices.Contains(i)) continue;
                        var vertex = vertices[i];
                        var v = transform.TransformPoint(vertex);
                        v = Handles.FreeMoveHandle(v, Quaternion.identity, vertexSize, Vector3.zero, Handles.DotHandleCap);
                        if (GUI.changed)
                        {
                            v = transform.InverseTransformPoint(v);
                            if (findSameIndex)
                            {
                                var sameIndices = vertexConnection.GetSameIndices(vertex);
                                if (sameIndices == null)
                                {
                                    needToRebuildConnection = true;
                                }
                                else
                                {
                                    for (int j = sameIndices.Count - 1; j >= 0; --j)
                                    {
                                        vertices[sameIndices[j]] = v;
                                    }
                                    editMesh.vertices = vertices;
                                    editMesh.RecalculateNormals();
                                }
                            }
                            else
                            {
                                vertices[i] = v;
                                editMesh.vertices = vertices;
                                editMesh.RecalculateNormals();
                            }
                        }
                    }
                }
            }
            else if (hideNotSelected)
            // draw selected
            {
                int removeIndex = -1;
                foreach (var i in selectedIndex)
                {
                    if (ignoredList.Contains(i)) continue;
                    if (!sceneViewIndices.Contains(i)) continue;
                    if (DrawPoint(i, true))
                    {
                        removeIndex = i;
                        break;
                    }
                }
                if (removeIndex != -1)
                {
                    selectedIndex.Remove(removeIndex);
                }
            }
            else
            {
                if (hideSelected)
                // draw not selected
                {
                    for (int i = vertices.Length - 1; i >= 0; --i)
                    {
                        if (ignoredList.Contains(i)) continue;
                        if (selectedIndex.Contains(i)) continue;
                        if (!sceneViewIndices.Contains(i)) continue;
                        if (DrawPoint(i, false))
                        {
                            selectedIndex.Add(i);
                        }
                    }
                }
                else
                {
                    for (int i = vertices.Length - 1; i >= 0; --i)
                    {
                        if (ignoredList.Contains(i)) continue;
                        if (!sceneViewIndices.Contains(i)) continue;
                        bool selected = selectedIndex.Contains(i);
                        if (DrawPoint(i, selected))
                        {
                            if (selected) selectedIndex.Remove(i);
                            else selectedIndex.Add(i);
                        }
                    }
                }
            }

            Handles.color = Color.white;
        }

        SelectMultiPointsInSamePositionView();
        DrawConnection();

        // if (gizmoUnfold)
        // {
        //     UnfoldGizmos();
        // }

        if (transform == null && editMesh != null)
        {
            DestroyImmediate(editMesh);
            mesh = null;
            editMesh = null;
        }
    }

    public class SameVertexMap
    {
        public bool Error;
        readonly Dictionary<Vector3, List<int>> pointDict = new();
        public void Rebuild(Vector3[] verts)
        {
            Error = false;
            try
            {
                pointDict.Clear();
                for (int i = verts.Length - 1; i >= 0; --i)
                {
                    var v = verts[i];
                    if (!pointDict.ContainsKey(v))
                        pointDict[v] = new List<int>();
                    pointDict[v].Add(i);
                }
            }
            catch (System.Exception e)
            {
                Error = true;
                Debug.LogError(e);
            }
        }

        public List<int> GetSameIndices(Vector3 v)
        {
            if (!Error)
            {
                if (pointDict.ContainsKey(v))
                    return pointDict[v];
            }
            return null;
        }
    }

    [System.Serializable]
    public class SelectedIndicesCollection
    {
        public List<SelectedIndicesData> datas;
    }

    [System.Serializable]
    public struct SelectedIndicesData
    {
        public string name;
        public int[] data;
        HashSet<int> set;

        public SelectedIndicesData(string n, HashSet<int> s)
        {
            name = n;
            set = s;
            data = set.ToArray();
        }

        public HashSet<int> Set
        {
            get
            {
                if (set == null || set.Count != data.Length) set = new HashSet<int>(data);
                return set;
            }
        }
    }

    bool needToRebuildConnection = false;
    SameVertexMap vertexConnection = new();
    int[] triangles;
    Vector3[] vertices;
    Vector3[] refVertices;
    Vector3[] normals;
    Transform transform;
    Transform nextTransform;
    HashSet<int> selectedIndex;

    [SerializeField] List<SelectedIndicesData> savedSelections = new();

    void ResetEditMesh()
    {
        if (mesh != null && editMesh != null)
        {
            editMesh = Instantiate(mesh);
            //CloneMesh(mesh, editMesh);
            if (mf != null) mf.sharedMesh = editMesh;
            if (smr != null) smr.sharedMesh = editMesh;
            vertices = editMesh.vertices;
            triangles = editMesh.triangles;
        }
    }

    void Reset()
    {
        if (mesh != null)
        {
            if (mf != null) mf.sharedMesh = mesh;
            if (smr != null) smr.sharedMesh = mesh;
        }
    }

    public struct ConnectionTriangle
    {
        public int i0;
        public int i1;
        public int i2;

        public ConnectionTriangle(int i0, int i1, int i2, int mainIndex)
        {
            if (i0 == mainIndex)
            {
                this.i0 = i0;
                this.i1 = i1;
                this.i2 = i2;
            }
            else if (i1 == mainIndex)
            {
                this.i0 = i1;
                this.i1 = i0;
                this.i2 = i2;
            }
            else
            {
                this.i0 = i2;
                this.i1 = i0;
                this.i2 = i1;
            }
        }


        public override int GetHashCode()
        {
            return i0 ^ i1 ^ i2;
        }
    }
    HashSet<ConnectionTriangle> tempConnection = new();
    void GetConnection(int index)
    {
        tempConnection.Clear();
        for (int i = 0, imax = triangles.Length; i < imax; i += 3)
        {
            var i0 = triangles[i];
            var i1 = triangles[i + 1];
            var i2 = triangles[i + 2];
            if (i0 == index || i1 == index || i2 == index)
            {
                tempConnection.Add(new ConnectionTriangle(i0, i1, i2, index));
            }
        }
    }
    void ClearConnection()
    {
        tempConnection.Clear();
    }
    void DrawConnection()
    {
        if (tempConnection == null) tempConnection = new();
        if (tempConnection.Count == 0) return;
        var oldColor = Handles.color;
        Handles.color = Color.cyan;
        foreach (var tri in tempConnection)
        {
            Handles.DrawLine(
                transform.TransformPoint(vertices[tri.i0]),
                transform.TransformPoint(vertices[tri.i1])
            );
            Handles.DrawLine(
                transform.TransformPoint(vertices[tri.i0]),
                transform.TransformPoint(vertices[tri.i2])
            );
        }
        Handles.color = oldColor;
    }

    bool drawIndex;
    bool hideSelectedIndex;
    bool drawNormal;
    bool editMode;
    bool hideSelected;
    bool hideNotSelected;
    bool drawFrontOnly;
    bool editVertexSize;
    readonly bool editSelectSize;
    bool editNormalSize;
    bool drawNormalColor;
    bool hideVertex;
    bool hide;
    bool findSameIndex;
    // bool subindexMode;
    bool moveAllSelect;
    bool moveAllSelectDiffMode;
    bool rotateAllSelect;
    bool unfoldMode;
    // bool gizmoUnfold;
    MonoScript self;

    void DrawToolBar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        hide = GUILayout.Toggle(hide, "Hide", EditorStyles.toolbarButton);
        editMode = GUILayout.Toggle(editMode, "Edit Mesh", EditorStyles.toolbarButton);
        if (!editMode)
        {
            hideSelected = GUILayout.Toggle(hideSelected, "Hide Selected", EditorStyles.toolbarButton);
            hideNotSelected = GUILayout.Toggle(hideNotSelected, "Only Selected", EditorStyles.toolbarButton);
        }
        else
        {
            findSameIndex = GUILayout.Toggle(findSameIndex, "Find Same", EditorStyles.toolbarButton);
        }
        // subindexMode = GUILayout.Toggle(subindexMode, "Subindex Mode", EditorStyles.toolbarButton);
        drawFrontOnly = GUILayout.Toggle(drawFrontOnly, "Draw Front Only", EditorStyles.toolbarButton);

        GUILayout.FlexibleSpace();
        if (!drawNormal)
        {
            editVertexSize = GUILayout.Toggle(editVertexSize, "VertexSetting", EditorStyles.toolbarButton);
            if (editVertexSize)
            {
                hideVertex = GUILayout.Toggle(hideVertex, "Hide Vertex", EditorStyles.toolbarButton);
                vertexSize = EditorGUILayout.Slider(vertexSize, 1e-7f, 0.1f, GUILayout.Width(180f));
            }
        }
        else
        {
            editNormalSize = GUILayout.Toggle(editNormalSize, "NormalSetting", EditorStyles.toolbarButton);
            drawNormalColor = GUILayout.Toggle(drawNormalColor, "NorColor", EditorStyles.toolbarButton);
            if (editNormalSize)
            {
                normalSize = EditorGUILayout.Slider(normalSize, 0.0001f, 0.5f, GUILayout.Width(180f));
            }
        }
        if (hideNotSelected)
            hideSelectedIndex = GUILayout.Toggle(hideSelectedIndex, "Hide Index", EditorStyles.toolbarButton);
        else
        {
            hideSelectedIndex = GUILayout.Toggle(hideSelectedIndex, "Hide Select Index", EditorStyles.toolbarButton);
            drawIndex = GUILayout.Toggle(drawIndex, "Draw Index", EditorStyles.toolbarButton);
        }
        drawNormal = GUILayout.Toggle(drawNormal, "Draw Normal", EditorStyles.toolbarButton);
        if (GUI.changed) SceneView.RepaintAll();
        if (self == null) self = FindScript();
        EditorGUILayout.ObjectField(self, typeof(MonoScript), false, GUILayout.Width(80f));
        EditorGUILayout.EndHorizontal();
    }

    readonly bool lastMaskIsReadable;
    readonly Texture2D mask;
    string textureFile;
    string exportTexturePath;
    int exportTextureSize = 256;
    SphereCollider sphereCollider;
    GameObject findMeshTarget;
    readonly HashSet<int> ignoredList = new();

    int findIndex;
    int selectStIndex;
    int selectEdIndex;
    string output;
    string selectionName;
    string save_edited_path;
    Vector2 scrollPos = new();

    static Texture2D ReadAsTexture(string file)
    {
        try
        {
            byte[] bytes = System.IO.File.ReadAllBytes(file);
            Texture2D texture = new(1, 1);
            texture.LoadImage(bytes);
            return texture;
        }
        catch (System.Exception)
        { }
        return null;
    }

    static HashSet<int> ReadTextureAsIndexHash(Texture2D texture, Vector2[] uvs)
    {
        HashSet<int> indices = new();

        for (int i = uvs.Length - 1; i >= 0; --i)
        {
            var col = texture.GetPixelBilinear(uvs[i].x, uvs[i].y);
            if (col.r != 0f)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    static void ExportIndexToTexture(string output, int textureSize, HashSet<int> indices, Vector2[] uvs)
    {
        var ext = System.IO.Path.GetExtension(output);
        HashSet<string> supported = new() { ".jpg", ".png", ".tga", ".exr" };
        if (!supported.Contains(ext))
            throw new System.Exception($"Input file ext: ({ext}) is not supported. Supported format are (jpg, png).");
        Texture2D tex = new(textureSize, textureSize);
        var colors = tex.GetPixels32();
        for (int i = colors.Length - 1; i >= 0; --i)
        {
            colors[i] = new Color32(0, 0, 0, 255);
        }
        tex.SetPixels32(colors);
        tex.Apply();
        foreach (var i in indices)
        {
            int x = (int)(uvs[i].x * textureSize);
            int y = (int)(uvs[i].y * textureSize);
            tex.SetPixel(x, y, Color.white);
        }
        tex.Apply();

        byte[] bytes = null;

        switch (ext)
        {
            case ".jpg":
                bytes = tex.EncodeToJPG();
                break;
            case ".png":
                bytes = tex.EncodeToPNG();
                break;
            case ".tga":
                bytes = tex.EncodeToTGA();
                break;
            case ".exr":
                bytes = tex.EncodeToEXR();
                break;
        }
        if (bytes != null)
        {
            if (System.IO.File.Exists(output))
                System.IO.File.Delete(output);
            System.IO.File.WriteAllBytes(output, bytes);
        }
    }

    private void OnGUI()
    {
        DrawToolBar();
        bool build = false;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Current Base Mesh : ", GUILayout.Width(160f));
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.ObjectField(mesh, typeof(Mesh), false);
        EditorGUI.EndDisabledGroup();
        if (mesh != null && GUILayout.Button("reload"))
            build = true;
        EditorGUILayout.EndHorizontal();

        if (nextMesh != null)
        {
            if (mesh != null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Next Mesh : ");
                EditorGUILayout.ObjectField(nextMesh, typeof(Mesh), false);
                if (GUILayout.Button("Replace", GUILayout.Width(80f)))
                {
                    if (mf != null) mf.sharedMesh = mesh;
                    if (smr != null) smr.sharedMesh = mesh;
                    mesh = nextMesh;
                    transform = nextTransform;
                    mf = nextMf;
                    smr = nextSmr;
                    nextMesh = null;
                    nextTransform = null;
                    build = true;
                    SceneView.RepaintAll();
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                mesh = nextMesh;
                transform = nextTransform;
                mf = nextMf;
                smr = nextSmr;
                nextMesh = null;
                nextTransform = null;
                build = true;
            }
        }

        if (editMesh != null)
        {
            try
            {
                if (editMode)
                {
                    if (GUILayout.Button("Reset Mesh"))
                    {
                        ResetEditMesh();
                        SceneView.RepaintAll();
                    }
                    if (selectedIndex.Count > 0 && GUILayout.Button("Set all selected To 0"))
                    {
                        foreach (var i in selectedIndex)
                        {
                            vertices[i] = Vector3.zero;
                        }
                        editMesh.vertices = vertices;
                        editMesh.RecalculateNormals();
                        SceneView.RepaintAll();
                    }

                    EditorGUILayout.BeginHorizontal();
                    var last = moveAllSelectDiffMode;
                    moveAllSelectDiffMode = GUILayout.Toggle(moveAllSelectDiffMode, "Move all selected vertex");
                    if (moveAllSelectDiffMode != last)
                    {
                        if (unfoldMode)
                        {
                            UnfoldUpdate();
                            unfoldMovingIndex = -1;
                        }
                        if (moveAllSelectDiffMode)
                        {
                            MoveAllStart();
                        }
                        rotateAllSelect = unfoldMode = moveAllSelect = false;
                    }
                    last = moveAllSelect;
                    moveAllSelect = GUILayout.Toggle(moveAllSelect, "Move all selected vertex to same position");
                    if (moveAllSelect != last)
                    {
                        if (unfoldMode)
                        {
                            UnfoldUpdate();
                            unfoldMovingIndex = -1;
                        }
                        rotateAllSelect = moveAllSelectDiffMode = unfoldMode = false;
                    }

                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    last = rotateAllSelect;
                    rotateAllSelect = GUILayout.Toggle(rotateAllSelect, "Rotate Mode");
                    if (rotateAllSelect != last)
                    {
                        if (unfoldMode)
                        {
                            UnfoldUpdate();
                            unfoldMovingIndex = -1;
                        }
                        if (rotateAllSelect)
                        {
                            RotateAllStart();
                        }
                        unfoldMode = moveAllSelect = moveAllSelectDiffMode = false;
                    }
                    if (GUILayout.Button("Reset Rotate"))
                    {
                        rotateAllQuat = Quaternion.identity;
                        RotateUpdate();
                    }
                    EditorGUILayout.EndHorizontal();

                    last = unfoldMode;
                    unfoldMode = GUILayout.Toggle(unfoldMode, "Unfold Mode");
                    if (unfoldMode != last)
                    {
                        if (unfoldMode)
                        {
                            UnfoldStart(refVertices ?? vertices);
                        }
                        rotateAllSelect = moveAllSelect = moveAllSelectDiffMode = false;
                    }
                    // gizmoUnfold = GUILayout.Toggle(gizmoUnfold, "gizmoUnfold");

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Unfold reference mesh");
                    var _findMeshTarget = (GameObject)EditorGUILayout.ObjectField(findMeshTarget, typeof(GameObject), true);
                    if (_findMeshTarget != findMeshTarget)
                    {
                        findMeshTarget = _findMeshTarget;
                        if (findMeshTarget)
                        {
                            Mesh tempMesh = null;
                            var mf = findMeshTarget.GetComponent<MeshFilter>();
                            if (mf) tempMesh = mf.sharedMesh;
                            if (!tempMesh)
                            {
                                var smr = findMeshTarget.GetComponent<SkinnedMeshRenderer>();
                                if (smr) tempMesh = smr.sharedMesh;
                            }
                            if (!tempMesh)
                            {
                                Debug.Log($"Cannot find Mesh in GameObject: {findMeshTarget}");
                            }
                            else
                            {
                                refVertices = tempMesh.vertices;
                                if (refVertices.Length != vertices.Length)
                                {
                                    Debug.Log($"refVertices.Length ({refVertices.Length}) != vertices.Length ({vertices.Length})");
                                    refVertices = null;
                                }
                            }
                        }
                        else
                        {
                            refVertices = null;
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    save_edited_path = EditorGUILayout.TextField("output path : ", save_edited_path);
                    if (GUILayout.Button("Save"))
                    {
                        WriteObj.Write(save_edited_path, editMesh);
                    }
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    bool hasSelection = selectedIndex.Count > 0;
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Print Selection"))
                    {
                        if (hasSelection)
                        {
                            System.Text.StringBuilder sb = new();
                            foreach (var i in selectedIndex)
                            {
                                sb.AppendFormat("{0}, ", i);
                            }
                            sb.Length -= 2;
                            Debug.Log(sb.ToString());
                        }
                        else
                        {
                            Debug.Log("No Selections");
                        }
                    }
                    if (GUILayout.Button("Print Distance"))
                    {
                        if (selectedIndex.Count == 2)
                        {
                            bool first = true;
                            Vector3 v1 = Vector3.zero, v2 = Vector3.zero;
                            foreach (var i in selectedIndex)
                            {
                                if (first)
                                {
                                    first = false;
                                    v1 = vertices[i];
                                }
                                else
                                    v2 = vertices[i];
                            }
                            Debug.Log("distance : " + (v2 - v1).magnitude);
                        }
                        else
                        {
                            Debug.Log("Selected vertices not equals 2");
                        }
                    }

                    if (drawNormal)
                    {
                        if (GUILayout.Button("Print Selected Normal"))
                        {
                            if (hasSelection)
                            {
                                System.Text.StringBuilder sb = new();
                                foreach (var i in selectedIndex)
                                {
                                    sb.AppendFormat("{0}:{1}, ", i, string.Format("({0}, {1}, {2})", normals[i].x, normals[i].y, normals[i].z));
                                }
                                sb.Length -= 2;
                                Debug.Log(sb.ToString());
                            }
                            else
                            {
                                Debug.Log("No Selections");
                            }
                        }
                        if (GUILayout.Button("Recalc normals"))
                        {
                            if (editMesh != null)
                            {
                                editMesh.RecalculateNormals();
                                normals = editMesh.normals;
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Print Selected Vertex Position"))
                    {
                        if (hasSelection)
                        {
                            System.Text.StringBuilder sb = new();
                            foreach (var i in selectedIndex)
                            {
                                sb.AppendFormat("({0}, {1}, {2})\n", vertices[i].x, vertices[i].y, vertices[i].z);
                            }
                            Debug.Log(sb.ToString());
                        }
                        else
                        {
                            Debug.Log("No Selections");
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Focus") && hasSelection)
                    {
                        var pos = Vector3.zero;
                        float count = 0f;
                        foreach (var i in selectedIndex)
                        {
                            pos += transform.TransformPoint(vertices[i]);
                            count++;
                        }
                        pos /= count;
                        SceneView.lastActiveSceneView.Frame(new Bounds(pos, 100f * vertexSize * Vector3.one), false);
                    }

                    //if (GUILayout.Button("Create position at selection"))
                    //{
                    //	CreateGameObjectAtSelection();
                    //	SceneView.RepaintAll();
                    //}

                    if (GUILayout.Button("Clear Selection"))
                    {
                        selectedIndex.Clear();
                        ignoredList.Clear();
                        SceneView.RepaintAll();
                    }
                    EditorGUILayout.EndHorizontal();

                    scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUI.skin.box);
                    EditorGUILayout.LabelField("Saved Selections");
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Add new selection name : ", GUILayout.Width(160f));
                    selectionName = EditorGUILayout.TextField(selectionName);
                    if (GUILayout.Button("Add"))
                    {
                        var n = selectionName;
                        if (string.IsNullOrEmpty(n)) n = "unnamed " + savedSelections.Count;
                        savedSelections.Add(new SelectedIndicesData(n, selectedIndex));
                    }
                    EditorGUILayout.EndHorizontal();

                    int removeIndex = -1;
                    for (int i = 0, max = savedSelections.Count; i < max; ++i)
                    {
                        var selection = savedSelections[i];
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button(selection.name, GUILayout.MinWidth(160f)))
                        {
                            selectedIndex = selection.Set;
                            SceneView.RepaintAll();
                        }
                        if (hasSelection && GUILayout.Button("replace", GUILayout.Width(60f)))
                        {
                            if (EditorUtility.DisplayDialog("Replace", "You going to replace old saved indices (You cannot undo this action). Are you sure?", "Yes", "No"))
                                savedSelections[i] = new SelectedIndicesData(selection.name, selectedIndex);
                        }
                        if (GUILayout.Button("remove", GUILayout.Width(60f)))
                        {
                            if (EditorUtility.DisplayDialog("Remove", "You going to remove saved indices (You cannot undo this action). Are you sure?", "Yes", "No"))
                                removeIndex = i;
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    if (removeIndex != -1)
                    {
                        savedSelections.RemoveAt(removeIndex);
                        SceneView.RepaintAll();
                    }
                    EditorGUILayout.EndScrollView();

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("select texture file");
                    textureFile = EditorGUILayout.TextField(textureFile);
                    if (GUILayout.Button("load"))
                    {
                        if (System.IO.File.Exists(textureFile))
                        {
                            var tex = ReadAsTexture(textureFile);
                            selectedIndex = ReadTextureAsIndexHash(tex, mesh.uv);
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    if (selectedIndex != null && selectedIndex.Count > 0)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("output selected into texture file");
                        exportTexturePath = EditorGUILayout.TextField(exportTexturePath);
                        EditorGUILayout.LabelField("texture size:", GUILayout.Width(100f));
                        exportTextureSize = EditorGUILayout.IntField(exportTextureSize, GUILayout.Width(100f));
                        if (GUILayout.Button("export"))
                        {
                            ExportIndexToTexture(exportTexturePath, exportTextureSize, selectedIndex, mesh.uv);
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("select from sphere collider");
                    sphereCollider = (SphereCollider)EditorGUILayout.ObjectField(sphereCollider, typeof(SphereCollider), true);
                    if (sphereCollider != null)
                    {
                        if (GUILayout.Button("select"))
                        {
                            SelectFromSphereCollider(sphereCollider);
                            SceneView.RepaintAll();
                        }
                        if (GUILayout.Button("unselect"))
                        {
                            UnselectFromSphereCollider(sphereCollider);
                            SceneView.RepaintAll();
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    // testFile = EditorGUILayout.TextField(testFile);
                    // testFile2 = EditorGUILayout.TextField(testFile2);
                    // if (!string.IsNullOrEmpty(testFile) && !string.IsNullOrEmpty(testFile2))
                    // {
                    // 	if (GUILayout.Button("Test"))
                    // 	{
                    // 		selectedIndex.Clear();
                    // 		//LoadSelectIndex(testFile, selectedIndex);
                    // 		var uv = mesh.uv;
                    // 		//foreach (var index in selectedIndex)
                    // 		//{
                    // 		//	var v = vertices[index];
                    // 		//	for (int i = 0, max = vertices.Length; i < max; ++i)
                    // 		//	{
                    // 		//		if (i == index) continue;
                    // 		//		var v2 = vertices[i];
                    // 		//		if ((v2 - v).sqrMagnitude < Vector3.kEpsilon)
                    // 		//		{
                    // 		//			selectedIndex.Add(i);
                    // 		//		}
                    // 		//	}
                    // 		//}

                    // 		//selectedIndex = XRAvatarReal.Utilities.ReadTextureAsIndexHash(testFile, uv);
                    // 		var hash2 = XRAvatarReal.Utilities.ReadTextureAsIndexHash(testFile2, uv);
                    // 		foreach (var i in hash2)
                    // 		{
                    // 			selectedIndex.Add(i);
                    // 		}
                    // 	}
                    // }

                    // if (hasSelection && subindexMode)
                    // {
                    //     EditorGUILayout.BeginVertical(GUI.skin.box);
                    //     EditorGUILayout.LabelField("subindex mode");
                    //     EditorGUILayout.BeginHorizontal();
                    //     if (GUILayout.Button("Select from selected"))
                    //     {
                    //         foreach (var i in selectedIndex)
                    //             subIndex.Add(i);
                    //     }
                    //     if (GUILayout.Button("Clear"))
                    //     {
                    //         subIndex.Clear();
                    //     }
                    //     EditorGUILayout.EndHorizontal();
                    //     EditorGUILayout.EndVertical();
                    // }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError(e);
            }
        }

        GUILayout.FlexibleSpace();
        try
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Select vertex range  start :", GUILayout.Width(160f));
            selectStIndex = EditorGUILayout.IntField(selectStIndex);
            EditorGUILayout.LabelField("end :", GUILayout.Width(60f));
            selectEdIndex = EditorGUILayout.IntField(selectEdIndex);
            if (GUILayout.Button("select range", GUILayout.Width(160f)))
            {
                for (int i = selectStIndex; i < selectEdIndex; ++i)
                    selectedIndex.Add(i);
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Select vertex by index : ", GUILayout.Width(160f));
            findIndex = EditorGUILayout.IntField(findIndex);
            if (GUILayout.Button("select", GUILayout.Width(60f)))
            {
                selectedIndex.Add(findIndex);
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Save/ Load indices file : ", GUILayout.Width(160f));
            output = EditorGUILayout.TextField(output);
            if (GUILayout.Button("output", GUILayout.Width(60f)))
            {
                // if (subindexMode)
                //     ExportSubIndex(output, selectedIndex, subIndex);
                // else
                ExportSelectIndex(output, selectedIndex);
            }
            if (GUILayout.Button("load", GUILayout.Width(60f)))
            {
                selectedIndex.Clear();
                // if (subindexMode)
                //     LoadSelectIndex(output, subIndex);
                // else
                LoadSelectIndex(output, selectedIndex);
            }
            EditorGUILayout.EndHorizontal();
        }
        catch (System.Exception)
        { }

        if (build)
        {
            if (editMesh != null) DestroyImmediate(editMesh);
            editMesh = Instantiate(mesh);
            //editMesh = new Mesh();
            //editMesh.name = mesh.name + " (cloned)";
            //CloneMesh(mesh, editMesh);
            vertices = editMesh.vertices;
            triangles = editMesh.triangles;
            normals = editMesh.normals;
            if (vertexConnection == null)
            {
                vertexConnection = new SameVertexMap();
            }
            needToRebuildConnection = true;
            if (mf != null) mf.sharedMesh = editMesh;
            if (smr != null) smr.sharedMesh = editMesh;
        }

        if (needToRebuildConnection)
        {
            RebuildConnection();
        }
    }

    void RebuildConnection()
    {
        vertexConnection.Rebuild(vertices);
        needToRebuildConnection = false;
    }

    void CloneMesh(Mesh from, Mesh to)
    {
        to.vertices = from.vertices;
        to.triangles = from.triangles;
        to.uv = from.uv;
        to.uv2 = from.uv2;
        to.uv3 = from.uv3;
        to.uv4 = from.uv4;
        to.uv5 = from.uv5;
        to.uv6 = from.uv6;
        to.uv7 = from.uv7;
        to.uv8 = from.uv8;
        to.colors32 = from.colors32;
        to.bounds = from.bounds;
        to.normals = from.normals;
        to.tangents = to.tangents;

        List<BoneWeight> weights = new();
        from.GetBoneWeights(weights);
        to.boneWeights = weights.ToArray();
        List<Matrix4x4> bindposes = new();
        from.GetBindposes(bindposes);
        to.bindposes = bindposes.ToArray();

        //to.indexFormat = from.indexFormat;

        //to.RecalculateNormals();
    }

    void SelectFromSphereCollider(SphereCollider s)
    {
        var pos = s.transform.TransformPoint(s.center);
        var r = s.radius * s.radius;
        for (int i = vertices.Length - 1; i >= 0; --i)
        {
            var dir = pos - transform.TransformPoint(vertices[i]);
            if (dir.sqrMagnitude < r)
                selectedIndex.Add(i);
        }
    }
    void UnselectFromSphereCollider(SphereCollider s)
    {
        var pos = s.transform.TransformPoint(s.center);
        var r = s.radius * s.radius;
        List<int> list = new();
        foreach (var i in selectedIndex)
        {
            var dir = pos - transform.TransformPoint(vertices[i]);
            if (dir.sqrMagnitude < r)
                list.Add(i);
        }
        foreach (var i in list)
        {
            selectedIndex.Remove(i);
        }
    }

    void ExportSelectIndex(string file, HashSet<int> set)
    {
        System.Text.StringBuilder sb = new();
        foreach (var i in set)
        {
            sb.AppendFormat("{0}\n", i);
        }
        sb.Remove(sb.Length - 1, 1);
        System.IO.File.WriteAllText(file, sb.ToString());
    }

    void LoadSelectIndex(string file, HashSet<int> indices)
    {
        var lines = System.IO.File.ReadAllLines(file);
        if (lines.Length == 1) lines = lines[0].Split(',');
        for (int i = 0, max = lines.Length; i < max; ++i)
        {
            if (string.IsNullOrEmpty(lines[i]))
                continue;
            indices.Add(int.Parse(lines[i]));
        }
    }

    int[] GetIndiceMap(string file)
    {
        var text = System.IO.File.ReadAllText(file);
        var chars = text.Split(',');
        List<int> indices = new();
        for (int i = 0, max = chars.Length; i < max; ++i)
        {
            if (string.IsNullOrEmpty(chars[i]))
                continue;
            indices.Add(int.Parse(chars[i]));
        }

        return indices.ToArray();
    }

    void CreateGameObjectAtSelection()
    {
        Vector3 sum = Vector3.zero;
        float count = 0f;
        foreach (var i in selectedIndex)
        {
            var vertex = vertices[i];
            var v = transform.TransformPoint(vertex);
            count++;
            sum += v;
        }
        GameObject go = new();
        go.transform.position = sum / count;
        Selection.activeGameObject = go;
    }

    MonoScript FindScript()
    {
        return MonoScript.FromScriptableObject(this);
    }
}
