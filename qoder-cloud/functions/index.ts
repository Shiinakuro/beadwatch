import { createDatabaseClient, createSiteContext, errorResponse } from './_qoder/backend.mjs';
import { application } from './app.mjs';

// The public origin is a fixed, non-secret site constant. Browser writes must
// match it; native clients use Bearer tokens and do not rely on cookies.
const ORIGIN = 'https://beadwatch-cloud-7c28q17g8xc.qoder.zone';
const handle = application({ db: createDatabaseClient(), origin: ORIGIN });

Deno.serve(async (request) => {
  let context;
  try {
    context = createSiteContext(request, { forwardApplicationCredentials: true });
    return await handle(context);
  } catch (error) {
    return errorResponse(error, context?.requestId);
  }
});
