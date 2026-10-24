// 3D view of the web app: a port of the WPF SceneBuilder, MeshFactory, TextureFactory and CameraController.
// The server (Designer.razor) owns the project; this module draws it, handles the camera and turns
// mouse/keyboard input into calls back to the server. Units are millimetres; X right, Y up, Z towards the viewer.
import * as THREE from 'three';

// WPF lights and blends in sRGB without gamma; do the same so colours match the desktop app.
THREE.ColorManagement.enabled = false;

const FIELD_OF_VIEW = 45;          // horizontal, degrees (WPF semantics)
const ORTHOGRAPHIC_STAND_OFF = 30_000;
const MEASURE_SNAP = 40;           // mm: measure points snap to a board corner this close
const GRID_STEP = 500;
const TEXTURE_SIZE = 512;
const SELECTION_COLOR = 0xff8c00;
const EDGE_COLOR = 0x2d2823;
const SELECTED_EMISSIVE = 0x462300; // tint, texture stays visible

let v = null; // the one viewport on the page

// ---- public API (called from .NET) ---------------------------------------------------------

export function init(host, dotnet, catalog) {
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.outputColorSpace = THREE.LinearSRGBColorSpace;
    renderer.setPixelRatio(window.devicePixelRatio);
    renderer.setClearColor(0x000000, 0);
    host.querySelector('.viewport-canvas').appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    addLights(scene);
    const roomGroup = new THREE.Group();
    const boardsGroup = new THREE.Group();
    scene.add(roomGroup, boardsGroup);

    v = {
        host, dotnet, renderer, scene, roomGroup, boardsGroup,
        overlay: host.querySelector('.viewport-overlay'),
        viewName: host.querySelector('.viewport-name'),
        perspective: new THREE.PerspectiveCamera(45, 1, 50, 200_000),
        orthographic: new THREE.OrthographicCamera(-1, 1, 1, -1, 10, 200_000),
        cam: { target: new THREE.Vector3(), yaw: 30, pitch: 25, distance: 7000, ortho: false },
        viewLabel: '3D',
        data: null, version: -1, roomKey: null, framed: false,
        boardObjects: new Map(), // board id -> THREE.Group
        boardMeshes: [], roomMeshes: [], disposables: [],
        options: { tool: 'Select', snap: 10, showEdges: true, showDimensions: true, busy: false },
        mode: 'none', last: null, drag: null, pendingCommit: null,
        measureA: null, measureB: null,
        dirty: false, renderQueued: false,
    };

    new ResizeObserver(resize).observe(host);
    resize();
    attachInput();
    renderThumbnails(catalog);
}

/// Called after every server render: scene (only when it changed), tool options, title and dirty flag.
export function sync(payload) {
    if (!v)
        return;
    if (payload.options)
        setOptions(payload.options);
    if (payload.scene)
        setScene(payload.scene);
    if (payload.title)
        document.title = payload.title;
    v.dirty = payload.dirty;
    decorate();
}

/// View menu and toolbar commands. Returns whether the camera is orthographic (for the menu check).
export function command(name) {
    const c = v.cam;
    switch (name) {
        case '3d': setView('3D', 30, 25, false); break;
        case 'front': setView('Front', 0, 0, true); break;
        case 'top': setView('Top', 0, 89.5, true); break;
        case 'left': setView('Left', -90, 0, true); break;
        case 'right': setView('Right', 90, 0, true); break;
        case 'zoomIn': c.distance *= 0.88; break;
        case 'zoomOut': c.distance /= 0.88; break;
        case 'toggleOrtho': c.ortho = !c.ortho; break;
        case 'zoomRoom': if (v.data) frame(v.data.room); break;
        case 'clearMeasure': v.measureA = v.measureB = null; break;
    }
    updateCamera();
    return c.ortho;
}

export function download(fileName, text, mime) {
    const url = URL.createObjectURL(new Blob([text], { type: mime }));
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function focusViewport() {
    v?.host.focus();
}

// ---- options and scene ---------------------------------------------------------------------

function setOptions(options) {
    const edgesChanged = v.options.showEdges !== options.showEdges;
    v.options = options;
    v.host.dataset.tool = options.tool;
    if (edgesChanged && v.data)
        rebuildBoards();
    requestRender();
}

function setScene(data) {
    v.data = data;
    v.version = data.version;
    if (v.pendingCommit !== null && data.version >= v.pendingCommit) {
        v.pendingCommit = null; // the server now has the drag; drop the local offset
        v.drag = null;
    }

    const room = data.room;
    const roomKey = [room.width, room.depth, room.height, room.floorColor, room.wallColor].join('|');
    if (roomKey !== v.roomKey) {
        v.roomKey = roomKey;
        buildRoom(room);
    }
    if (!v.framed) {
        v.framed = true;
        frame(room);
    }
    rebuildBoards();
}

function rebuildBoards() {
    for (const d of v.disposables)
        d.dispose();
    v.disposables = [];
    v.boardsGroup.clear();
    v.boardObjects.clear();
    v.boardMeshes = [];

    const { boards, materials, selection } = v.data;
    const materialById = new Map(materials.map(m => [m.id, m]));
    const selected = new Set(selection);
    const groupBoxes = new Map();
    for (const b of boards) {
        if (b.groupId)
            groupBoxes.set(b.groupId, union(groupBoxes.get(b.groupId), bounds(b)));
    }

    for (const board of boards) {
        const material = materialById.get(board.materialId) ?? { id: board.materialId, color: '#CCCCCC', opacity: 1, texture: 'Solid', textureSize: 1000 };
        const isSelected = selected.has(board.id);
        const group = new THREE.Group();
        group.userData.boardId = board.id;
        for (const mesh of boardMeshes(board, material, isSelected, board.groupId ? groupBoxes.get(board.groupId) : null)) {
            mesh.userData.boardId = board.id;
            group.add(mesh);
            v.boardMeshes.push(mesh);
        }
        if (isSelected || v.options.showEdges)
            group.add(edges(bounds(board), isSelected ? SELECTION_COLOR : EDGE_COLOR));
        v.boardsGroup.add(group);
        v.boardObjects.set(board.id, group);
    }
    applyDragOffset();
    requestRender();
}

/// The board's faces, plus a separate face with frame/panel shading for the outside of a styled door or drawer front.
function boardMeshes(board, material, selected, groupBox) {
    const b = bounds(board);
    const sizes = [b.maxX - b.minX, b.maxY - b.minY, b.maxZ - b.minZ];
    const grain = sizes.indexOf(Math.max(...sizes));
    const thin = sizes.indexOf(Math.min(...sizes));
    const offset = hashString(board.id) % 997 / 997 * material.textureSize;

    const styled = (board.kind === 'Door' || board.kind === 'DrawerFront') && board.frontStyle !== 'Flat';
    // The outside of a front is the side away from the centre of its cabinet.
    let outerSign = 1;
    if (styled && groupBox) {
        const boardCenter = [(b.minX + b.maxX) / 2, (b.minY + b.maxY) / 2, (b.minZ + b.maxZ) / 2];
        const groupCenter = [(groupBox.minX + groupBox.maxX) / 2, (groupBox.minY + groupBox.maxY) / 2, (groupBox.minZ + groupBox.maxZ) / 2];
        outerSign = boardCenter[thin] >= groupCenter[thin] ? 1 : -1;
    }

    const mesh = new MeshBuilder();
    for (let axis = 0; axis < 3; axis++) {
        for (const sign of [-1, 1]) {
            if (!(styled && axis === thin && sign === outerSign))
                mesh.addFace(b, axis, sign, grain, material.textureSize, offset);
        }
    }
    const result = [boardMesh(mesh, { map: surfaceTexture(material), color: material.color }, material.opacity, selected)];

    if (styled) {
        const face = new MeshBuilder();
        face.addFace(b, thin, outerSign, grain, 0, 0);
        // Face size in mm as drawn: width along the horizontal axis, height along Y
        // (a front lying flat is drawn with its longer side as width).
        const [width, height] = thin === 0 ? [sizes[2], sizes[1]]
            : thin === 2 ? [sizes[0], sizes[1]]
            : [Math.max(sizes[0], sizes[2]), Math.min(sizes[0], sizes[2])];
        result.push(boardMesh(face, { map: frontTexture(material, board.frontStyle, width, height) }, material.opacity, selected));
    }
    return result;
}

function boardMesh(builder, { map, color }, opacity, selected) {
    const geometry = builder.geometry();
    const material = new THREE.MeshPhongMaterial({
        map: map ?? null,
        color: map ? 0xffffff : parseColor(color),
        specular: 0x282828,
        shininess: 40,
        emissive: selected ? SELECTED_EMISSIVE : 0x000000,
        transparent: opacity < 1,
        opacity,
        depthWrite: opacity >= 1,
        // Push faces back a little so the edge lines on them stay visible.
        polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
    });
    v.disposables.push(geometry, material);
    return new THREE.Mesh(geometry, material);
}

function edges(b, color) {
    const box = new THREE.BoxGeometry(b.maxX - b.minX, b.maxY - b.minY, b.maxZ - b.minZ);
    const geometry = new THREE.EdgesGeometry(box);
    box.dispose();
    geometry.translate((b.minX + b.maxX) / 2, (b.minY + b.maxY) / 2, (b.minZ + b.maxZ) / 2);
    const material = new THREE.LineBasicMaterial({ color });
    v.disposables.push(geometry, material);
    return new THREE.LineSegments(geometry, material);
}

function addLights(scene) {
    // WPF light colours; three.js divides diffuse light by pi, so scale it back.
    scene.add(new THREE.AmbientLight(0x646464, Math.PI));
    const key = new THREE.DirectionalLight(0xc3beb4, Math.PI);
    key.position.set(0.4, 1, 0.6);
    const fill = new THREE.DirectionalLight(0x55555f, Math.PI);
    fill.position.set(-0.6, 0.3, -0.8);
    scene.add(key, fill);
}

function buildRoom(room) {
    for (const child of v.roomGroup.children) {
        child.geometry.dispose();
        child.material.dispose();
    }
    v.roomGroup.clear();
    const w = room.width, d = room.depth, h = room.height;
    const floorColor = parseColor(room.floorColor, '#D2B48C');
    const wallColor = parseColor(room.wallColor, '#F5F5F5');
    const solid = (builder, color) => {
        const mesh = new THREE.Mesh(builder.geometry(), new THREE.MeshLambertMaterial({ color }));
        v.roomGroup.add(mesh);
        return mesh;
    };
    const P = (x, y, z) => new THREE.Vector3(x, y, z);

    const floor = new MeshBuilder();
    floor.addQuad(P(0, 0, 0), P(0, 0, d), P(w, 0, d), P(w, 0, 0), P(0, 1, 0));

    // Walls face inwards and are single-sided, so a wall between the camera and the room is culled ("dollhouse" view).
    const walls = new MeshBuilder();
    walls.addQuad(P(0, 0, 0), P(w, 0, 0), P(w, h, 0), P(0, h, 0), P(0, 0, 1));
    walls.addQuad(P(0, 0, d), P(w, 0, d), P(w, h, d), P(0, h, d), P(0, 0, -1));
    walls.addQuad(P(0, 0, 0), P(0, 0, d), P(0, h, d), P(0, h, 0), P(1, 0, 0));
    walls.addQuad(P(w, 0, 0), P(w, 0, d), P(w, h, d), P(w, h, 0), P(-1, 0, 0));
    v.roomMeshes = [solid(floor, floorColor), solid(walls, wallColor)];

    // 500 mm grid on the floor and walls, as in PRO100.
    const line = 4, lift = 1;
    const floorGrid = new MeshBuilder();
    for (let x = GRID_STEP; x < w; x += GRID_STEP)
        floorGrid.addQuad(P(x - line / 2, lift, 0), P(x - line / 2, lift, d), P(x + line / 2, lift, d), P(x + line / 2, lift, 0), P(0, 1, 0));
    for (let z = GRID_STEP; z < d; z += GRID_STEP)
        floorGrid.addQuad(P(0, lift, z - line / 2), P(w, lift, z - line / 2), P(w, lift, z + line / 2), P(0, lift, z + line / 2), P(0, 1, 0));
    solid(floorGrid, floorColor.clone().multiplyScalar(0.85));

    const wallGrid = new MeshBuilder();
    for (let y = GRID_STEP; y < h; y += GRID_STEP) {
        wallGrid.addQuad(P(0, y - line / 2, lift), P(w, y - line / 2, lift), P(w, y + line / 2, lift), P(0, y + line / 2, lift), P(0, 0, 1));
        wallGrid.addQuad(P(lift, y - line / 2, 0), P(lift, y - line / 2, d), P(lift, y + line / 2, d), P(lift, y + line / 2, 0), P(1, 0, 0));
        wallGrid.addQuad(P(w - lift, y - line / 2, 0), P(w - lift, y - line / 2, d), P(w - lift, y + line / 2, d), P(w - lift, y + line / 2, 0), P(-1, 0, 0));
    }
    for (let x = GRID_STEP; x < w; x += GRID_STEP)
        wallGrid.addQuad(P(x - line / 2, 0, lift), P(x + line / 2, 0, lift), P(x + line / 2, h, lift), P(x - line / 2, h, lift), P(0, 0, 1));
    for (let z = GRID_STEP; z < d; z += GRID_STEP) {
        wallGrid.addQuad(P(lift, 0, z - line / 2), P(lift, 0, z + line / 2), P(lift, h, z + line / 2), P(lift, h, z - line / 2), P(1, 0, 0));
        wallGrid.addQuad(P(w - lift, 0, z - line / 2), P(w - lift, 0, z + line / 2), P(w - lift, h, z + line / 2), P(w - lift, h, z - line / 2), P(-1, 0, 0));
    }
    solid(wallGrid, wallColor.clone().multiplyScalar(0.9));
}

// ---- meshes --------------------------------------------------------------------------------

class MeshBuilder {
    positions = [];
    normals = [];
    uvs = [];
    indices = [];

    /// A quad (corners in loop order) whose front face points along normal.
    addQuad(a, b, c, d, normal) {
        // Front faces are counter-clockwise; flip the loop if it winds the wrong way.
        if (new THREE.Vector3().crossVectors(b.clone().sub(a), c.clone().sub(a)).dot(normal) < 0)
            [b, d] = [d, b];
        const i = this.positions.length / 3;
        for (const p of [a, b, c, d]) {
            this.positions.push(p.x, p.y, p.z);
            this.normals.push(normal.x, normal.y, normal.z);
            this.uvs.push(0, 0);
        }
        this.indices.push(i, i + 1, i + 2, i, i + 2, i + 3);
    }

    /// One face of a box with texture coordinates. Axis 0/1/2 = X/Y/Z; sign picks the min or max side.
    /// Tiled mode: coordinates in tiles of `tile` mm with u along the grain axis where it lies in the face.
    /// Relative mode (tile = 0): 0..1 across the face, u horizontal and v = 0 at the top.
    addFace(box, axis, sign, grainAxis, tile, offset) {
        const min = [box.minX, box.minY, box.minZ];
        const max = [box.maxX, box.maxY, box.maxZ];
        const a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
        let uAxis, vAxis;
        if (tile <= 0 && (a1 === 1 || a2 === 1))
            [uAxis, vAxis] = a1 === 1 ? [a2, a1] : [a1, a2];
        else if (grainAxis === a1 || grainAxis === a2)
            [uAxis, vAxis] = grainAxis === a1 ? [a1, a2] : [a2, a1];
        else
            [uAxis, vAxis] = max[a1] - min[a1] >= max[a2] - min[a2] ? [a1, a2] : [a2, a1];

        const points = [], uvs = [];
        for (const [cu, cv] of [[0, 0], [1, 0], [1, 1], [0, 1]]) {
            const p = [0, 0, 0];
            p[axis] = sign > 0 ? max[axis] : min[axis];
            p[uAxis] = cu === 0 ? min[uAxis] : max[uAxis];
            p[vAxis] = cv === 0 ? min[vAxis] : max[vAxis];
            points.push(new THREE.Vector3(...p));
            uvs.push(tile > 0
                ? [(p[uAxis] + offset) / tile, (p[vAxis] + offset * 0.37) / tile]
                : [(p[uAxis] - min[uAxis]) / Math.max(1e-9, max[uAxis] - min[uAxis]),
                   vAxis === 1 ? (max[1] - p[1]) / Math.max(1e-9, max[1] - min[1]) : (p[vAxis] - min[vAxis]) / Math.max(1e-9, max[vAxis] - min[vAxis])]);
        }

        const normal = new THREE.Vector3(axis === 0 ? sign : 0, axis === 1 ? sign : 0, axis === 2 ? sign : 0);
        if (new THREE.Vector3().crossVectors(points[1].clone().sub(points[0]), points[2].clone().sub(points[0])).dot(normal) < 0) {
            [points[1], points[3]] = [points[3], points[1]];
            [uvs[1], uvs[3]] = [uvs[3], uvs[1]];
        }

        const i = this.positions.length / 3;
        for (let k = 0; k < 4; k++) {
            this.positions.push(points[k].x, points[k].y, points[k].z);
            this.normals.push(normal.x, normal.y, normal.z);
            this.uvs.push(uvs[k][0], uvs[k][1]);
        }
        this.indices.push(i, i + 1, i + 2, i, i + 2, i + 3);
    }

    geometry() {
        const g = new THREE.BufferGeometry();
        g.setAttribute('position', new THREE.Float32BufferAttribute(this.positions, 3));
        g.setAttribute('normal', new THREE.Float32BufferAttribute(this.normals, 3));
        g.setAttribute('uv', new THREE.Float32BufferAttribute(this.uvs, 2));
        g.setIndex(this.indices);
        return g;
    }
}

function bounds(b) {
    return { minX: b.x, minY: b.y, minZ: b.z, maxX: b.x + b.sizeX, maxY: b.y + b.sizeY, maxZ: b.z + b.sizeZ };
}

function union(a, b) {
    if (!a)
        return b;
    return {
        minX: Math.min(a.minX, b.minX), minY: Math.min(a.minY, b.minY), minZ: Math.min(a.minZ, b.minZ),
        maxX: Math.max(a.maxX, b.maxX), maxY: Math.max(a.maxY, b.maxY), maxZ: Math.max(a.maxZ, b.maxZ),
    };
}

function hashString(s) {
    let h = 0;
    for (let i = 0; i < s.length; i++)
        h = Math.imul(h, 31) + s.charCodeAt(i) | 0;
    return h >>> 0;
}

// ---- textures ------------------------------------------------------------------------------

const sources = new Map();   // material look -> canvas / image (or null for solid colours)
const textures = new Map();  // material look -> THREE.Texture for tiled board faces
const fronts = new Map();    // material look + style + size -> THREE.Texture for door fronts
const swatches = new Map();  // material look -> CSS background

function lookKey(m) {
    return `${m.texture}|${m.color}|${m.id}|${m.imagePath ?? ''}`;
}

/// Canvas or loaded image for a material, or null when it is a plain colour (or an image still loading).
function textureSource(m) {
    const key = lookKey(m);
    if (sources.has(key)) {
        const s = sources.get(key);
        return s instanceof HTMLImageElement && !s.complete ? null : s;
    }
    let source = null;
    if (m.texture === 'Wood' || m.texture === 'Stone') {
        source = generate(parseRgb(m.color), m.texture === 'Wood' ? wood : stone, seed(m.id));
    } else if (m.texture === 'Image' && /^(data:image\/|https?:)/.test(m.imagePath ?? '')) {
        source = new Image();
        source.onload = () => {
            textures.delete(key);
            for (const k of [...fronts.keys()].filter(k => k.startsWith(key)))
                fronts.delete(k);
            swatches.delete(key);
            if (v?.data)
                rebuildBoards();
            decorate();
        };
        source.src = m.imagePath;
    }
    sources.set(key, source);
    return source instanceof HTMLImageElement ? null : source;
}

/// Tiled texture for texture coordinates measured in tiles (1 = one TextureSize).
function surfaceTexture(m) {
    const key = lookKey(m);
    if (textures.has(key))
        return textures.get(key);
    const source = textureSource(m);
    const texture = source ? makeTexture(source) : null;
    if (source)
        textures.set(key, texture);
    return texture;
}

function makeTexture(source) {
    const t = new THREE.Texture(source);
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.flipY = false; // v = 0 at the top of the image, as in WPF
    t.anisotropy = v?.renderer.capabilities.getMaxAnisotropy() ?? 1;
    t.needsUpdate = true;
    return t;
}

/// Outside face of a door or drawer front, drawn in millimetres: the material plus frame and panel shading.
function frontTexture(m, style, width, height) {
    const key = `${lookKey(m)}|${style}|${Math.round(width)}|${Math.round(height)}|${m.textureSize}`;
    if (fronts.has(key))
        return fronts.get(key);

    const scale = TEXTURE_SIZE / Math.max(width, height);
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(width * scale));
    canvas.height = Math.max(1, Math.round(height * scale));
    const g = canvas.getContext('2d');
    g.scale(scale, scale);

    const source = textureSource(m);
    if (source) {
        const pattern = g.createPattern(source, 'repeat');
        pattern.setTransform(new DOMMatrix().scale(m.textureSize / source.width, m.textureSize / source.height));
        g.fillStyle = pattern;
    } else {
        g.fillStyle = cssColor(m.color);
    }
    g.fillRect(0, 0, width, height);

    const frame = Math.min(Math.max(Math.min(width, height) * 0.16, 25), 75);
    const panel = { x: frame, y: frame, w: Math.max(1, width - 2 * frame), h: Math.max(1, height - 2 * frame) };
    const dark = 'rgba(0,0,0,0.43)', darkSoft = 'rgba(0,0,0,0.22)', light = 'rgba(255,255,255,0.35)';
    switch (style) {
        case 'Shaker':
            // Recessed panel: shadow along the top/left inner edge, highlight bottom/right.
            g.fillStyle = darkSoft;
            g.fillRect(panel.x, panel.y, panel.w, panel.h);
            bevel(g, panel, 6, dark, light);
            break;
        case 'RaisedPanel': {
            const b = Math.min(Math.max(Math.min(panel.w, panel.h) * 0.12, 12), 40);
            bevel(g, panel, 5, dark, light);
            slopes(g, panel, { x: panel.x + b, y: panel.y + b, w: Math.max(1, panel.w - 2 * b), h: Math.max(1, panel.h - 2 * b) }, light, dark);
            break;
        }
        case 'Glass': {
            bevel(g, panel, 6, dark, light);
            const glass = g.createLinearGradient(panel.x, panel.y, panel.x + panel.w * 0.707, panel.y + panel.h * 0.707);
            glass.addColorStop(0, 'rgba(150,185,200,0.92)');
            glass.addColorStop(1, 'rgba(205,228,236,0.92)');
            g.fillStyle = glass;
            g.fillRect(panel.x, panel.y, panel.w, panel.h);
            break;
        }
    }

    const texture = new THREE.CanvasTexture(canvas);
    texture.flipY = false;
    texture.anisotropy = v?.renderer.capabilities.getMaxAnisotropy() ?? 1;
    if (source || m.texture !== 'Image')
        fronts.set(key, texture);
    return texture;
}

/// Thin shadow/highlight lines around a rectangle (light from top-left).
function bevel(g, r, w, shadow, highlight) {
    g.fillStyle = shadow;
    g.fillRect(r.x, r.y, r.w, w);
    g.fillRect(r.x, r.y, w, r.h);
    g.fillStyle = highlight;
    g.fillRect(r.x, r.y + r.h - w, r.w, w);
    g.fillRect(r.x + r.w - w, r.y, w, r.h);
}

/// The four sloped bevels of a raised panel between the outer and inner rectangle.
function slopes(g, o, i, lit, shaded) {
    const quad = (fill, ...points) => {
        g.fillStyle = fill;
        g.beginPath();
        points.forEach(([x, y], k) => k === 0 ? g.moveTo(x, y) : g.lineTo(x, y));
        g.closePath();
        g.fill();
    };
    const oR = o.x + o.w, oB = o.y + o.h, iR = i.x + i.w, iB = i.y + i.h;
    quad(lit, [o.x, o.y], [oR, o.y], [iR, i.y], [i.x, i.y]);
    quad(lit, [o.x, o.y], [i.x, i.y], [i.x, iB], [o.x, oB]);
    quad(shaded, [o.x, oB], [i.x, iB], [iR, iB], [oR, oB]);
    quad(shaded, [oR, o.y], [oR, oB], [iR, iB], [iR, i.y]);
}

/// CSS background for a material swatch: the top-left quarter of its texture, or its colour.
function swatchBackground(m) {
    const key = lookKey(m);
    if (swatches.has(key))
        return swatches.get(key);
    const source = textureSource(m);
    let background = cssColor(m.color);
    if (source) {
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = 128;
        canvas.getContext('2d').drawImage(source, 0, 0, source.width / 2, source.height / 2, 0, 0, 128, 128);
        background = `center / cover url(${canvas.toDataURL('image/jpeg', 0.85)})`;
    }
    if (source || m.texture !== 'Image')
        swatches.set(key, background);
    return background;
}

// Procedural textures (seamless: the noise is periodic over the image), the same maths as the desktop app.

function seed(id) {
    let h = 17;
    for (let i = 0; i < id.length; i++)
        h = Math.imul(h, 31) + id.charCodeAt(i) | 0;
    return h;
}

function generate([r, g, b], shade, seedValue) {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = TEXTURE_SIZE;
    const context = canvas.getContext('2d');
    const image = context.createImageData(TEXTURE_SIZE, TEXTURE_SIZE);
    const px = image.data;
    for (let y = 0; y < TEXTURE_SIZE; y++) {
        for (let x = 0; x < TEXTURE_SIZE; x++) {
            const f = shade(x, y, seedValue);
            const i = (y * TEXTURE_SIZE + x) * 4;
            px[i] = r * f;
            px[i + 1] = g * f;
            px[i + 2] = b * f;
            px[i + 3] = 255;
        }
    }
    context.putImageData(image, 0, 0);
    return canvas;
}

/// Grain runs along X: wavy growth rings plus fine pores.
function wood(x, y, s) {
    const u = x / TEXTURE_SIZE, w = y / TEXTURE_SIZE;
    const warp = fbm(u, w, 3, 3, 6, s);
    const rings = Math.sin((w * 18 + warp * 3.2) * Math.PI * 2); // 18 whole rings: seamless in v
    const pores = fbm(u, w, 2, 64, 256, s + 7 | 0);
    const streaks = fbm(u, w, 4, 4, 48, s + 3 | 0);
    return 0.86 + 0.07 * rings + 0.10 * (streaks - 0.5) - 0.08 * Math.max(0, pores - 0.62);
}

function stone(x, y, s) {
    const u = x / TEXTURE_SIZE, w = y / TEXTURE_SIZE;
    const cloud = fbm(u, w, 4, 4, 4, s);
    const grain = fbm(u, w, 1, 96, 96, s + 5 | 0);
    const speck = grain > 0.80 ? -0.45 : grain < 0.12 ? 0.25 : 0;
    return 0.88 + 0.22 * (cloud - 0.5) + speck;
}

function fbm(u, w, octaves, cellsX, cellsY, s) {
    let sum = 0, amplitude = 0.5, total = 0;
    for (let o = 0; o < octaves; o++) {
        sum += amplitude * valueNoise(u * cellsX, w * cellsY, cellsX, cellsY, s + o * 101 | 0);
        total += amplitude;
        cellsX *= 2;
        cellsY *= 2;
        amplitude *= 0.5;
    }
    return sum / total;
}

function valueNoise(x, y, periodX, periodY, s) {
    const x0 = Math.floor(x), y0 = Math.floor(y);
    const fx = x - x0, fy = y - y0;
    const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
    const a = hash(x0, y0, periodX, periodY, s), b = hash(x0 + 1, y0, periodX, periodY, s);
    const c = hash(x0, y0 + 1, periodX, periodY, s), d = hash(x0 + 1, y0 + 1, periodX, periodY, s);
    return (a + (b - a) * sx) * (1 - sy) + (c + (d - c) * sx) * sy;
}

function hash(x, y, periodX, periodY, s) {
    x = ((x % periodX) + periodX) % periodX;
    y = ((y % periodY) + periodY) % periodY;
    let h = (Math.imul(x, 374761393) + Math.imul(y, 668265263) | 0) + Math.imul(s, 2147483647) >>> 0;
    h = Math.imul(h ^ (h >>> 13), 1274126177) >>> 0;
    return ((h ^ (h >>> 16)) & 0xFFFF) / 65535;
}

const colorProbe = document.createElement('canvas').getContext('2d');

/// Any CSS colour (#RRGGBB, names) to #rrggbb; the fallback when it is not a colour.
function cssColor(value, fallback = '#D3D3D3') {
    colorProbe.fillStyle = fallback;
    if (value)
        colorProbe.fillStyle = value;
    const result = colorProbe.fillStyle;
    return result.startsWith('#') ? result : fallback;
}

function parseColor(value, fallback) {
    return new THREE.Color(cssColor(value, fallback));
}

function parseRgb(value) {
    const hex = cssColor(value).slice(1);
    return [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16));
}

// ---- catalog thumbnails and swatches -------------------------------------------------------

const thumbnails = new Map();

function renderThumbnails({ templates, materials }) {
    const width = 120, height = 104, ratio = 2;
    const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
    renderer.outputColorSpace = THREE.LinearSRGBColorSpace;
    renderer.setPixelRatio(ratio);
    renderer.setSize(width, height, false);
    renderer.setClearColor(0xffffff, 1);
    const materialById = new Map(materials.map(m => [m.id, m]));

    // Thumbnail meshes go to the shared disposables list; keep the viewport's own list aside meanwhile.
    const own = v.disposables;
    for (const { key, boards } of templates) {
        v.disposables = [];
        const scene = new THREE.Scene();
        addLights(scene);
        let box = null;
        const groupBox = boards.reduce((acc, b) => union(acc, bounds(b)), null);
        for (const board of boards) {
            box = union(box, bounds(board));
            const material = materialById.get(board.materialId) ?? { id: board.materialId, color: '#CCCCCC', opacity: 1, texture: 'Solid', textureSize: 1000 };
            for (const mesh of boardMeshes(board, material, false, groupBox))
                scene.add(mesh);
            scene.add(edges(bounds(board), EDGE_COLOR));
        }
        box ??= { minX: 0, minY: 0, minZ: 0, maxX: 1, maxY: 1, maxZ: 1 };
        const target = new THREE.Vector3((box.minX + box.maxX) / 2, (box.minY + box.maxY) / 2, (box.minZ + box.maxZ) / 2);
        const size = Math.max(box.maxX - box.minX, box.maxY - box.minY, box.maxZ - box.minZ);
        const direction = new THREE.Vector3(0.55, 0.38, 1).normalize();
        const aspect = width / height;
        const camera = new THREE.PerspectiveCamera(verticalFov(32, aspect), aspect, 10, 100_000);
        camera.position.copy(target).addScaledVector(direction, size * 2.3);
        camera.lookAt(target);
        renderer.render(scene, camera);
        thumbnails.set(key, renderer.domElement.toDataURL('image/png'));
        for (const d of v.disposables)
            d.dispose();
    }
    v.disposables = own;
    renderer.dispose();
    renderer.forceContextLoss();
}

/// Fills catalog thumbnails and material swatches the server rendered as placeholders.
function decorate() {
    for (const img of document.querySelectorAll('img[data-thumb]')) {
        const src = thumbnails.get(img.dataset.thumb);
        if (src && img.getAttribute('src') !== src)
            img.src = src;
    }
    for (const el of document.querySelectorAll('[data-swatch]')) {
        const m = {
            id: el.dataset.id ?? '', color: el.dataset.color, texture: el.dataset.texture,
            imagePath: el.dataset.image || null,
        };
        const key = lookKey(m);
        if (el.dataset.applied === key)
            continue;
        const background = swatchBackground(m);
        el.style.background = background;
        if (m.texture !== 'Image' || swatches.has(key))
            el.dataset.applied = key;
    }
}

// ---- camera --------------------------------------------------------------------------------

function verticalFov(horizontalDegrees, aspect) {
    return 2 * Math.atan(Math.tan(horizontalDegrees * Math.PI / 360) / aspect) * 180 / Math.PI;
}

function offsetDirection() {
    const yaw = v.cam.yaw * Math.PI / 180, pitch = v.cam.pitch * Math.PI / 180;
    return new THREE.Vector3(Math.cos(pitch) * Math.sin(yaw), Math.sin(pitch), Math.cos(pitch) * Math.cos(yaw));
}

function forward() {
    return offsetDirection().negate();
}

function basis() {
    const f = forward();
    const right = new THREE.Vector3().crossVectors(f, new THREE.Vector3(0, 1, 0)).normalize();
    const up = new THREE.Vector3().crossVectors(right, f).normalize();
    return { right, up };
}

/// Width of the visible area at the target distance, in millimetres.
function visibleWidth() {
    return 2 * v.cam.distance * Math.tan(FIELD_OF_VIEW * Math.PI / 360);
}

function camera() {
    return v.cam.ortho ? v.orthographic : v.perspective;
}

function updateCamera() {
    const c = v.cam;
    c.pitch = Math.min(Math.max(c.pitch, -89.5), 89.5);
    c.distance = Math.min(Math.max(c.distance, 300), 60_000);
    const offset = offsetDirection();
    const aspect = v.width / Math.max(1, v.height);

    v.perspective.fov = verticalFov(FIELD_OF_VIEW, aspect);
    v.perspective.aspect = aspect;
    v.perspective.position.copy(c.target).addScaledVector(offset, c.distance);
    v.perspective.lookAt(c.target);
    v.perspective.updateProjectionMatrix();

    const half = visibleWidth() / 2;
    Object.assign(v.orthographic, { left: -half, right: half, top: half / aspect, bottom: -half / aspect });
    v.orthographic.position.copy(c.target).addScaledVector(offset, ORTHOGRAPHIC_STAND_OFF);
    v.orthographic.lookAt(c.target);
    v.orthographic.updateProjectionMatrix();

    const status = document.getElementById('camera-status');
    if (status)
        status.textContent = `${c.ortho ? 'Orthographic' : 'Perspective'}  yaw ${Math.round(c.yaw)}°  pitch ${Math.round(c.pitch)}°`;
    v.viewName.textContent = v.viewLabel;
    requestRender();
}

function setView(label, yaw, pitch, ortho) {
    v.viewLabel = label;
    Object.assign(v.cam, { yaw, pitch, ortho });
}

/// Centres the camera on the room.
function frame(room) {
    v.cam.target.set(room.width / 2, room.height * 0.35, room.depth / 2);
    v.cam.distance = Math.max(room.width, room.depth, room.height) * 1.7;
    updateCamera();
}

function resize() {
    const rect = v.host.getBoundingClientRect();
    v.width = Math.max(1, rect.width);
    v.height = Math.max(1, rect.height);
    v.renderer.setSize(v.width, v.height);
    updateCamera();
}

function requestRender() {
    if (v.renderQueued)
        return;
    v.renderQueued = true;
    requestAnimationFrame(() => {
        v.renderQueued = false;
        v.renderer.render(v.scene, camera());
        updateOverlay();
    });
}

// ---- picking -------------------------------------------------------------------------------

const raycaster = new THREE.Raycaster();

function ray(event) {
    const rect = v.host.getBoundingClientRect();
    const ndc = new THREE.Vector2(
        (event.clientX - rect.left) / rect.width * 2 - 1,
        1 - (event.clientY - rect.top) / rect.height * 2);
    raycaster.setFromCamera(ndc, camera());
    return raycaster.ray;
}

/// Nearest board under the mouse: { id, point }.
function hitBoard(event) {
    ray(event);
    const hit = raycaster.intersectObjects(v.boardMeshes, false)[0];
    return hit ? { id: hit.object.userData.boardId, point: hit.point } : null;
}

/// Nearest surface facing the camera (board, wall or floor) for the measure tool; on a board the point snaps to a nearby corner.
function hitSurface(event) {
    ray(event);
    const hit = raycaster.intersectObjects([...v.boardMeshes, ...v.roomMeshes], false)[0];
    if (!hit)
        return null;
    const point = hit.point.clone();
    const board = hit.object.userData.boardId && v.data.boards.find(b => b.id === hit.object.userData.boardId);
    if (board) {
        const b = bounds(board);
        let best = null;
        for (const x of [b.minX, b.maxX])
            for (const y of [b.minY, b.maxY])
                for (const z of [b.minZ, b.maxZ]) {
                    const corner = new THREE.Vector3(x, y, z);
                    if (!best || corner.distanceTo(point) < best.distanceTo(point))
                        best = corner;
                }
        if (best.distanceTo(point) <= MEASURE_SNAP)
            return best;
    }
    return point;
}

function intersectPlane(r, planePoint, planeNormal) {
    const denominator = r.direction.dot(planeNormal);
    if (Math.abs(denominator) < 1e-9)
        return null;
    const t = planePoint.clone().sub(r.origin).dot(planeNormal) / denominator;
    return t < 0 ? null : r.origin.clone().addScaledVector(r.direction, t);
}

// ---- mouse and keyboard --------------------------------------------------------------------

function attachInput() {
    const host = v.host;
    host.addEventListener('contextmenu', e => e.preventDefault());
    host.addEventListener('pointerdown', onPointerDown);
    host.addEventListener('pointermove', onPointerMove);
    host.addEventListener('pointerup', onPointerUp);
    host.addEventListener('pointercancel', () => { v.mode = 'none'; v.drag = null; applyDragOffset(); });
    host.addEventListener('wheel', e => {
        e.preventDefault();
        v.cam.distance *= e.deltaY < 0 ? 0.88 : 1 / 0.88;
        updateCamera();
    }, { passive: false });

    // Drag and drop from the catalog.
    host.addEventListener('dragover', e => {
        if (e.dataTransfer.types.includes('application/x-fd-template') && !v.options.busy) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
        }
    });
    host.addEventListener('drop', e => {
        const key = e.dataTransfer.getData('application/x-fd-template');
        if (!key)
            return;
        e.preventDefault();
        const floor = intersectPlane(ray(e), new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 1, 0));
        if (floor)
            v.dotnet.invokeMethodAsync('ViewportDrop', key, floor.x, floor.z);
        host.focus();
    });
    document.addEventListener('dragstart', e => {
        const item = e.target.closest?.('[data-template]');
        if (item) {
            e.dataTransfer.setData('application/x-fd-template', item.dataset.template);
            e.dataTransfer.effectAllowed = 'copy';
        }
    });

    document.addEventListener('keydown', onKeyDown);
    window.addEventListener('beforeunload', e => {
        if (v.dirty) {
            e.preventDefault();
            e.returnValue = '';
        }
    });
}

function onPointerDown(e) {
    v.host.focus();
    v.last = { x: e.clientX, y: e.clientY };
    if (e.button === 1 || (e.button === 2 && e.shiftKey))
        v.mode = 'pan';
    else if (e.button === 2)
        v.mode = 'orbit';
    else if (e.button === 0)
        onLeftClick(e);
    if (v.mode !== 'none')
        v.host.setPointerCapture(e.pointerId);
    e.preventDefault();
}

function onLeftClick(e) {
    if (v.options.busy)
        return;
    switch (v.options.tool) {
        case 'Select': beginSelectOrDrag(e); break;
        case 'Paint': {
            const hit = hitBoard(e);
            if (hit)
                v.dotnet.invokeMethodAsync('ViewportPaint', hit.id, e.shiftKey);
            break;
        }
        case 'Eyedropper': {
            const hit = hitBoard(e);
            if (hit)
                v.dotnet.invokeMethodAsync('ViewportPick', hit.id);
            break;
        }
        case 'Measure': {
            const point = hitSurface(e);
            if (point) {
                if (!v.measureA || v.measureB)
                    [v.measureA, v.measureB] = [point, null];
                else
                    v.measureB = point;
                requestRender();
            }
            break;
        }
    }
}

async function beginSelectOrDrag(e) {
    const hit = hitBoard(e);
    const ctrl = e.ctrlKey || e.metaKey;
    // Drag the selection on a horizontal plane through the clicked point, or vertically with Shift.
    const vertical = e.shiftKey;
    let normal = new THREE.Vector3(0, 1, 0);
    if (vertical) {
        const f = forward();
        normal = new THREE.Vector3(f.x, 0, f.z);
        if (normal.length() < 1e-6)
            normal.set(0, 0, 1);
        normal.normalize();
    }
    const pointerId = e.pointerId;
    v.mode = 'pending';
    v.host.setPointerCapture(pointerId);

    const ids = await v.dotnet.invokeMethodAsync('ViewportClick', hit?.id ?? null, ctrl, e.altKey, e.shiftKey);
    if (v.mode !== 'pending') // released while the server answered: it was a plain click
        return;
    if (!hit || ctrl || ids.length === 0) {
        v.mode = 'none';
        return;
    }
    v.mode = 'drag';
    v.drag = { start: hit.point.clone(), normal, vertical, ids: new Set(ids), offset: new THREE.Vector3() };
}

function onPointerMove(e) {
    const dx = e.clientX - (v.last?.x ?? e.clientX), dy = e.clientY - (v.last?.y ?? e.clientY);
    v.last = { x: e.clientX, y: e.clientY };
    switch (v.mode) {
        case 'orbit':
            v.viewLabel = '3D';
            v.cam.yaw -= dx * 0.4;
            v.cam.pitch += dy * 0.4;
            updateCamera();
            break;
        case 'pan': {
            const { right, up } = basis();
            const scale = visibleWidth() / Math.max(1, v.width);
            v.cam.target.addScaledVector(right, -dx * scale).addScaledVector(up, dy * scale);
            updateCamera();
            break;
        }
        case 'drag': dragSelection(e); break;
        case 'none':
            if (v.options.tool === 'Measure' && v.measureA && !v.measureB) {
                // Rubber band from the first point.
                v.rubberBand = hitSurface(e);
                requestRender();
            }
            break;
    }
}

function dragSelection(e) {
    const d = v.drag;
    const current = intersectPlane(ray(e), d.start, d.normal);
    if (!current)
        return;
    const moved = current.sub(d.start);
    const snap = value => v.options.snap > 0 ? Math.round(value / v.options.snap) * v.options.snap : value;
    const target = d.vertical ? new THREE.Vector3(0, snap(moved.y), 0) : new THREE.Vector3(snap(moved.x), 0, snap(moved.z));
    if (target.equals(d.offset))
        return;
    d.offset = target;
    applyDragOffset();
    requestRender();
}

/// Moves the dragged boards locally until the server has applied the move.
function applyDragOffset() {
    const d = v.drag;
    for (const [id, group] of v.boardObjects)
        group.position.copy(d && d.ids.has(id) ? d.offset : new THREE.Vector3());
}

async function onPointerUp(e) {
    if (v.host.hasPointerCapture(e.pointerId))
        v.host.releasePointerCapture(e.pointerId);
    const d = v.mode === 'drag' ? v.drag : null;
    v.mode = 'none';
    if (!d)
        return;
    if (d.offset.lengthSq() === 0) {
        v.drag = null;
        return;
    }
    // Keep the offset on screen until the scene with the moved boards arrives.
    v.pendingCommit = Infinity;
    const version = await v.dotnet.invokeMethodAsync('ViewportMove', d.offset.x, d.offset.y, d.offset.z);
    if (v.version >= version) {
        v.pendingCommit = null;
        v.drag = null;
        applyDragOffset();
        requestRender();
    } else {
        v.pendingCommit = version;
        waitForCommit(d);
    }
}

function waitForCommit(d) {
    if (v.pendingCommit === null) {
        if (v.drag === d)
            v.drag = null;
        applyDragOffset();
        requestRender();
        return;
    }
    requestAnimationFrame(() => waitForCommit(d));
}

function onKeyDown(e) {
    if (e.target.id === 'ai-input' && e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        document.getElementById('ai-send')?.click();
        return;
    }
    if (e.target.closest?.('input, textarea, select, [contenteditable]') || document.querySelector('.modal-backdrop'))
        return;

    const ctrl = e.ctrlKey || e.metaKey;
    const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    const send = () => {
        e.preventDefault();
        v.dotnet.invokeMethodAsync('ViewportKey', key, ctrl, e.shiftKey);
    };

    if (ctrl) {
        if (['z', 'y', 'd', 'a', 's'].includes(key))
            send();
        else if (key === 'o') {
            e.preventDefault();
            document.getElementById('open-file')?.click();
        }
        return;
    }

    const views = { F5: '3d', F6: 'front', F7: 'top', F8: 'left', Home: 'zoomRoom' };
    if (views[key]) {
        e.preventDefault();
        command(views[key]);
        v.dotnet.invokeMethodAsync('ViewportOrthoChanged', v.cam.ortho);
        return;
    }
    if (key === 'F1') {
        send();
        return;
    }

    // Editing keys only while the 3D view has focus, so lists and fields keep their own keys.
    if (!v.host.contains(document.activeElement))
        return;
    if (key === 'Escape' && v.measureA) {
        v.measureA = v.measureB = v.rubberBand = null;
        requestRender();
        return;
    }
    if (['Delete', 'r', 's', 'p', 'i', 'm', 'Escape', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown'].includes(key))
        send();
}

// ---- 2D overlay: selection dimensions and the measure line ---------------------------------

const SVG = 'http://www.w3.org/2000/svg';

function updateOverlay() {
    const overlay = v.overlay;
    overlay.replaceChildren();
    const svg = document.createElementNS(SVG, 'svg');
    svg.setAttribute('width', v.width);
    svg.setAttribute('height', v.height);
    overlay.appendChild(svg);

    if (v.options.showDimensions && v.data) {
        const selected = new Set(v.data.selection);
        let b = null;
        for (const board of v.data.boards) {
            if (!selected.has(board.id))
                continue;
            const box = bounds(board);
            const o = v.drag && v.drag.ids.has(board.id) ? v.drag.offset : null;
            b = union(b, o ? { minX: box.minX + o.x, minY: box.minY + o.y, minZ: box.minZ + o.z, maxX: box.maxX + o.x, maxY: box.maxY + o.y, maxZ: box.maxZ + o.z } : box);
        }
        if (b) {
            // Width along the bottom front edge, height up the front left edge, depth along the bottom left edge.
            const P = (x, y, z) => new THREE.Vector3(x, y, z);
            dimension(svg, P(b.minX, b.minY, b.maxZ), P(b.maxX, b.minY, b.maxZ), b.maxX - b.minX, '#ff8c00');
            dimension(svg, P(b.minX, b.minY, b.maxZ), P(b.minX, b.maxY, b.maxZ), b.maxY - b.minY, '#ff8c00');
            dimension(svg, P(b.minX, b.minY, b.minZ), P(b.minX, b.minY, b.maxZ), b.maxZ - b.minZ, '#ff8c00');
        }
    }
    if (v.measureA) {
        const end = v.measureB ?? v.rubberBand ?? v.measureA;
        dimension(svg, v.measureA, end, end.distanceTo(v.measureA), '#4169e1', true);
    }
}

function dimension(svg, from, to, millimetres, color, showDots = false) {
    const p1 = project(from), p2 = project(to);
    if (!p1 || !p2)
        return;
    const line = document.createElementNS(SVG, 'line');
    Object.entries({ x1: p1.x, y1: p1.y, x2: p2.x, y2: p2.y, stroke: color, 'stroke-width': 1.5 }).forEach(([k, val]) => line.setAttribute(k, val));
    svg.appendChild(line);
    if (showDots) {
        for (const p of [p1, p2]) {
            const dot = document.createElementNS(SVG, 'circle');
            Object.entries({ cx: p.x, cy: p.y, r: 3.5, fill: color }).forEach(([k, val]) => dot.setAttribute(k, val));
            svg.appendChild(dot);
        }
    }
    if (millimetres < 1)
        return;
    const label = document.createElement('div');
    label.className = 'dimension-label';
    label.style.color = label.style.borderColor = color;
    label.style.left = `${(p1.x + p2.x) / 2}px`;
    label.style.top = `${(p1.y + p2.y) / 2}px`;
    label.textContent = String(Math.round(millimetres * 10) / 10);
    v.overlay.appendChild(label);
}

/// World point to viewport pixels, or null when it is not in front of the camera.
function project(point) {
    const cam = camera();
    if (point.clone().sub(cam.position).dot(forward()) <= 0)
        return null;
    const p = point.clone().project(cam);
    const x = (p.x + 1) / 2 * v.width, y = (1 - p.y) / 2 * v.height;
    return Number.isFinite(x) && Number.isFinite(y) ? { x, y } : null;
}
