import sharp from 'sharp';
import { writeFileSync } from 'fs';

const gold = '#E8B923';

function makeSvg(size) {
  // Scale from 24x24 viewBox to target size
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none">
    <path d="M12 2L4 7v10l8 5 8-5V7l-8-5z" stroke="${gold}" stroke-width="1.5" fill="none"/>
    <path d="M9 10l3-2 3 2v4l-3 2-3-2v-4z" fill="${gold}" opacity="0.3"/>
    <path d="M9 10l3-2 3 2v4l-3 2-3-2v-4z" stroke="${gold}" stroke-width="1" fill="none"/>
  </svg>`;
}

// Generate PNGs at standard icon sizes
const sizes = [16, 32, 48, 256];
const pngBuffers = [];

for (const size of sizes) {
  const svg = Buffer.from(makeSvg(size));
  const png = await sharp(svg).resize(size, size).png().toBuffer();
  pngBuffers.push({ size, png });
}

// Build .ico file
// ICO format: header + directory entries + image data
const numImages = pngBuffers.length;
const headerSize = 6;
const dirEntrySize = 16;
const dirSize = dirEntrySize * numImages;
let dataOffset = headerSize + dirSize;

// Header: reserved(2) + type(2) + count(2)
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);     // reserved
header.writeUInt16LE(1, 2);     // type: 1 = ICO
header.writeUInt16LE(numImages, 4);

const dirEntries = [];
const imageDataBuffers = [];

for (const { size, png } of pngBuffers) {
  const entry = Buffer.alloc(16);
  entry.writeUInt8(size === 256 ? 0 : size, 0);  // width (0 = 256)
  entry.writeUInt8(size === 256 ? 0 : size, 1);  // height
  entry.writeUInt8(0, 2);       // color palette
  entry.writeUInt8(0, 3);       // reserved
  entry.writeUInt16LE(1, 4);    // color planes
  entry.writeUInt16LE(32, 6);   // bits per pixel
  entry.writeUInt32LE(png.length, 8);   // image size
  entry.writeUInt32LE(dataOffset, 12);  // offset
  dirEntries.push(entry);
  imageDataBuffers.push(png);
  dataOffset += png.length;
}

const ico = Buffer.concat([header, ...dirEntries, ...imageDataBuffers]);
writeFileSync('trinketed.ico', ico);
console.log(`Generated trinketed.ico (${ico.length} bytes) with sizes: ${sizes.join(', ')}`);
