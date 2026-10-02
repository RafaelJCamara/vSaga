// VSAGA_API_URL points ng serve at another stack: an overlay's API port (5180 … 5680) or
// http://localhost:5275 for `dotnet run`.
const target = process.env.VSAGA_API_URL ?? 'http://localhost:5080';
export default {
  '/api/': { target },
  '/hubs/': { target, ws: true },
};
