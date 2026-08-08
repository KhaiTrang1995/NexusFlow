import { Page, PageHeader, Panel, PanelBody } from '@/design/primitives'
import { PublishedProcess } from './PublishedProcess'
import styles from './setup.module.css'

/**
 * The path a record walks, as the tenant published it.
 *
 * THIS SCREEN CONTRADICTED THE SAMPLE'S ONE CLAIM. Below the published process sat an editor over
 * the prototype's stages — a list this client was compiled with, on the page somebody opens
 * precisely to check that stages live in tables. Worse, its "Save the order" toasted "reordered"
 * and wrote nothing: the fixture list reordered in place, the real process did not move, and the
 * screen said it had. Every part of that is now gone.
 *
 * REORDERING IS PUBLISHING, AND THIS BUILD DOES NOT PUBLISH FROM THE BROWSER. A process is a
 * versioned definition; moving a stage is a new version, not an update, because an opportunity
 * already carries the stage it entered. The note below says so rather than offering a control
 * that would have to lie about what it did.
 */
export function StagesScreen() {
  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Stages" />

      <PublishedProcess />

      <Panel>
        <PanelBody>
          <p className={styles.sub}>
            <strong>Changing this is publishing a new version.</strong> A process is versioned
            because an opportunity carries the stage it entered: superseding a definition leaves
            those deals where they are, and editing one in place would move them somewhere nobody
            asked for. This build publishes over{' '}
            <span className={styles.mono}>POST /api/v1/crm/processes</span> and not from here — so
            what is above is what the tenant has, and there is nothing on this screen that claims
            to change it.
          </p>
        </PanelBody>
      </Panel>
    </Page>
  )
}
