// Generated from the fancy fish sheet: per cell (512 x 384, fish facing +x) the fish's
// extent x0..x1, its centre line, and the pectoral (front) and pelvic fins cut into fancy-fins.png - box [x0, y0, x1, y1]
// and pivot [x, y] where each joins the body.
(window.FISH_SHEETS = window.FISH_SHEETS || []).push({
  name: 'fancy', kind: 'fancy', cellW: 512, cellH: 384, cols: 4, rows: 2,
  cells: [
      { x0: 6, x1: 507, mid: 195, fins: [] },
      { x0: 6, x1: 507, mid: 170, fins: [] },
      { x0: 6, x1: 508, mid: 188, fins: [] },
      { x0: 5, x1: 508, mid: 181, fins: [] },
      { x0: 5, x1: 508, mid: 189, fins: [{ side: -1, front: true, box: [281, 47, 393, 106], pivot: [392, 108] }, { side: 1, front: true, box: [281, 267, 399, 340], pivot: [398, 264] }] },
      { x0: 5, x1: 507, mid: 195, fins: [{ side: -1, front: true, box: [293, 40, 413, 131], pivot: [412, 133] }, { side: 1, front: true, box: [280, 296, 392, 344], pivot: [391, 293] }] },
      { x0: 11, x1: 500, mid: 197, fins: [{ side: -1, front: true, box: [279, 10, 358, 65], pivot: [357, 67] }, { side: 1, front: true, box: [344, 285, 422, 367], pivot: [421, 282] }, { side: 1, front: false, box: [247, 285, 279, 380], pivot: [278, 282] }] },
      { x0: 4, x1: 508, mid: 193, fins: [{ side: -1, front: true, box: [280, 52, 395, 112], pivot: [394, 114] }, { side: 1, front: true, box: [289, 256, 406, 351], pivot: [405, 253] }] },
  ],
});
