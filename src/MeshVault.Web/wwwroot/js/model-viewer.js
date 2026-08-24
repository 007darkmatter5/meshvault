import * as THREE from '/lib/three/three.module.min.js';
import { OrbitControls } from '/lib/three/OrbitControls.js';

// Header layout must match MeshPayload.Build on the server.
const MAGIC = 'MVM1';
const HEADER_BYTES = 24;

const viewers = new Map();

function parsePayload(buffer) {
    const view = new DataView(buffer);

    const magic = String.fromCharCode(
        view.getUint8(0), view.getUint8(1), view.getUint8(2), view.getUint8(3));
    if (magic !== MAGIC) throw new Error('Unrecognised mesh payload');

    const triangleCount = view.getInt32(4, true);
    const min = new THREE.Vector3(
        view.getFloat32(8, true), view.getFloat32(12, true), view.getFloat32(16, true));
    const scale = view.getFloat32(20, true);

    const quantised = new Int16Array(buffer, HEADER_BYTES, triangleCount * 9);
    const positions = new Float32Array(triangleCount * 9);

    // Undo the server's quantisation: stored value is offset by -32768.
    for (let i = 0; i < positions.length; i += 3) {
        positions[i] = (quantised[i] + 32768) * scale + min.x;
        positions[i + 1] = (quantised[i + 1] + 32768) * scale + min.y;
        positions[i + 2] = (quantised[i + 2] + 32768) * scale + min.z;
    }

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));

    // Printable meshes are authored Z-up; three.js is Y-up. Without this the
    // model lies on its back and dragging sideways spins it about its
    // front-to-back axis, which feels like tumbling rather than turning.
    geometry.rotateX(-Math.PI / 2);

    // The payload carries no normals; flat faces suit printable geometry anyway.
    geometry.computeVertexNormals();
    geometry.computeBoundingBox();
    geometry.computeBoundingSphere();
    return { geometry, triangleCount };
}

class Viewer {
    constructor(canvas) {
        this.canvas = canvas;
        this.disposed = false;

        this.renderer = new THREE.WebGLRenderer({
            canvas,
            antialias: true,
            // Required so the canvas can be read back for snapshots.
            preserveDrawingBuffer: true,
        });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));

        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(0x1a1e25);

        this.camera = new THREE.PerspectiveCamera(45, 1, 0.1, 10000);

        this.controls = new OrbitControls(this.camera, canvas);
        // Damping gives the drag some weight instead of snapping frame to frame.
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.08;
        this.controls.rotateSpeed = 0.9;
        this.controls.zoomSpeed = 0.9;
        this.controls.panSpeed = 0.8;
        this.controls.screenSpacePanning = true;
        // Stop just short of the poles: going over the top flips the model and
        // reverses which way a sideways drag turns it.
        this.controls.minPolarAngle = 0.05;
        this.controls.maxPolarAngle = Math.PI - 0.05;
        this.controls.autoRotateSpeed = 1.2;

        // Lights follow the camera so the model is never lit from behind.
        this.key = new THREE.DirectionalLight(0xffffff, 2.0);
        this.fill = new THREE.DirectionalLight(0xffffff, 0.6);
        this.scene.add(this.key, this.fill, new THREE.AmbientLight(0xffffff, 0.55));

        // Any deliberate interaction cancels the idle spin.
        for (const event of ['pointerdown', 'wheel']) {
            canvas.addEventListener(event, () => { this.controls.autoRotate = false; }, { passive: true });
        }

        this.observer = new ResizeObserver(() => this.resize());
        this.observer.observe(canvas);

        this.resize();
        this.loop = this.loop.bind(this);
        requestAnimationFrame(this.loop);
    }

    setGeometry(geometry) {
        this.clearMesh();

        const material = new THREE.MeshPhongMaterial({
            color: 0x7ba7dd,
            shininess: 20,
            specular: 0x222222,
            // Printable meshes are often inconsistently wound, so draw both
            // sides rather than punching holes in the model.
            side: THREE.DoubleSide,
            flatShading: true,
        });

        this.mesh = new THREE.Mesh(geometry, material);

        // Sit the model on the origin with its centre above it, so orbiting
        // turns it on the spot like a turntable.
        const box = geometry.boundingBox;
        const centre = box.getCenter(new THREE.Vector3());
        this.mesh.position.set(-centre.x, -centre.y, -centre.z);

        this.radius = geometry.boundingSphere.radius || 1;
        this.scene.add(this.mesh);

        this.resetView();
    }

    /// Frames the model from a three-quarter view, the angle that reads best.
    resetView() {
        const radius = this.radius || 1;
        const distance = radius / Math.sin((this.camera.fov * Math.PI / 180) / 2) * 1.15;

        this.frame(distance * 0.55, distance * 0.42, distance * 0.72);
    }

    /// Restores the angle a snapshot was taken from. The stored position is a
    /// multiple of the bounding radius, so it reframes correctly on a model of
    /// a different size to the one it was saved on.
    applySavedView(view) {
        const radius = this.radius || 1;
        this.frame(view[0] * radius, view[1] * radius, view[2] * radius);
    }

    /// Points the camera at the origin from the given position, with near and
    /// far planes that suit the model rather than the position.
    frame(x, y, z) {
        const radius = this.radius || 1;
        const distance = radius / Math.sin((this.camera.fov * Math.PI / 180) / 2) * 1.15;

        this.camera.position.set(x, y, z);
        this.camera.near = Math.max(radius / 1000, 0.01);
        this.camera.far = Math.max(distance, Math.hypot(x, y, z)) * 10;
        this.camera.updateProjectionMatrix();

        this.controls.target.set(0, 0, 0);
        this.controls.minDistance = radius * 0.4;
        this.controls.maxDistance = distance * 6;
        this.controls.update();
    }

    /// The camera position as a multiple of the bounding radius, which is what
    /// gets stored alongside a snapshot.
    savedView() {
        const radius = this.radius || 1;
        const p = this.camera.position;
        return [p.x / radius, p.y / radius, p.z / radius];
    }

    clearMesh() {
        if (!this.mesh) return;
        this.scene.remove(this.mesh);
        this.mesh.geometry.dispose();
        this.mesh.material.dispose();
        this.mesh = null;
    }

    resize() {
        const width = this.canvas.clientWidth || 400;
        const height = this.canvas.clientHeight || 300;
        if (width === 0 || height === 0) return;

        this.renderer.setSize(width, height, false);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
    }

    loop() {
        if (this.disposed) return;

        this.controls.update();

        // Keep the key light over the viewer's shoulder.
        this.key.position.copy(this.camera.position);
        this.fill.position.copy(this.camera.position).negate();

        this.renderer.render(this.scene, this.camera);
        requestAnimationFrame(this.loop);
    }

    dispose() {
        this.disposed = true;
        this.observer?.disconnect();
        this.controls?.dispose();
        this.clearMesh();
        this.renderer.dispose();
    }
}

/// Generous, but finite: an uncached model on a slow share can take a minute,
/// and without a ceiling a stalled request left the spinner up forever.
const LOAD_TIMEOUT_MS = 180_000;

/// `savedView` is the camera stored with this model's snapshot, or null to use
/// the default framing.
export async function init(canvas, fileId, savedView) {
    dispose(canvas);

    const viewer = new Viewer(canvas);
    viewers.set(canvas, viewer);

    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(), LOAD_TIMEOUT_MS);

    let response;
    try {
        response = await fetch(`/mesh/${fileId}`, { signal: abort.signal });
    } catch (error) {
        if (error.name === 'AbortError') {
            throw new Error('Timed out reading this model from the library.');
        }
        throw error;
    } finally {
        clearTimeout(timer);
    }

    if (!response.ok) throw new Error(`Could not load mesh (${response.status})`);

    const { geometry, triangleCount } = parsePayload(await response.arrayBuffer());
    if (viewer.disposed) return 0;
    if (triangleCount === 0) return 0;

    viewer.setGeometry(geometry);

    // Opening on the angle the card image shows, so the model looks the way
    // the person who set it meant it to look.
    if (Array.isArray(savedView) && savedView.length === 3) viewer.applySavedView(savedView);

    return triangleCount;
}

export function resize(canvas) {
    viewers.get(canvas)?.resize();
}

export function resetView(canvas) {
    const viewer = viewers.get(canvas);
    if (!viewer) return;
    viewer.controls.autoRotate = false;
    viewer.resetView();
}

export function setAutoRotate(canvas, value) {
    const viewer = viewers.get(canvas);
    if (viewer) viewer.controls.autoRotate = value;
}

export function isAutoRotating(canvas) {
    return viewers.get(canvas)?.controls.autoRotate ?? false;
}

/// Snaps the camera to an axis-aligned view of the model.
export function setView(canvas, which) {
    const viewer = viewers.get(canvas);
    if (!viewer || !viewer.mesh) return;

    viewer.controls.autoRotate = false;

    const radius = viewer.radius || 1;
    const distance = radius / Math.sin((viewer.camera.fov * Math.PI / 180) / 2) * 1.15;

    // Y is up in the scene, because the geometry was rotated on load.
    const directions = {
        front: [0, 0, 1],
        back: [0, 0, -1],
        left: [-1, 0, 0],
        right: [1, 0, 0],
        top: [0, 1, 0.001],
        bottom: [0, -1, 0.001],
        iso: [0.55, 0.42, 0.72],
    };

    const d = directions[which] ?? directions.iso;
    const length = Math.hypot(d[0], d[1], d[2]);

    viewer.camera.position.set(
        d[0] / length * distance, d[1] / length * distance, d[2] / length * distance);
    viewer.controls.target.set(0, 0, 0);
    viewer.controls.update();
}

/// Captures the current frame and posts it as the model's card image. Posting
/// from the browser avoids base64-ing the PNG back through the Blazor circuit.
export async function saveSnapshot(canvas, modelId) {
    const viewer = viewers.get(canvas);
    if (!viewer) return false;

    viewer.renderer.render(viewer.scene, viewer.camera);

    const blob = await new Promise((resolve) =>
        viewer.renderer.domElement.toBlob(resolve, 'image/png'));
    if (!blob) return false;

    // The angle goes with the picture, so reopening the model can restore it.
    const [vx, vy, vz] = viewer.savedView();
    const query = `?vx=${vx.toFixed(4)}&vy=${vy.toFixed(4)}&vz=${vz.toFixed(4)}`;

    const response = await fetch(`/snapshot/${modelId}${query}`, {
        method: 'POST',
        headers: { 'Content-Type': 'image/png' },
        body: blob,
    });
    return response.ok;
}

export function dispose(canvas) {
    const viewer = viewers.get(canvas);
    if (!viewer) return;
    viewer.dispose();
    viewers.delete(canvas);
}
