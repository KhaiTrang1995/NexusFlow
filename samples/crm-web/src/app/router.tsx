import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router'
import { AppShell } from '@/shell/AppShell'
import { NotFound } from './NotFound'
import { ConsoleScreen } from '@/features/sales/ConsoleScreen'
import { KanbanScreen } from '@/features/sales/KanbanScreen'
import { ListScreen } from '@/features/sales/ListScreen'
import { RecordScreen } from '@/features/sales/RecordScreen'
import { QuoteBuilderScreen } from '@/features/sales/QuoteBuilderScreen'
import { CaseConsoleScreen } from '@/features/service/CaseConsoleScreen'
import { CaseBoardScreen } from '@/features/service/CaseBoardScreen'
import { SlaScreen } from '@/features/service/SlaScreen'
import { InboxScreen } from '@/features/work/InboxScreen'
import { ActivityScreen } from '@/features/work/ActivityScreen'
import { MobileScreen } from '@/features/work/MobileScreen'
import { ExecScreen } from '@/features/exec/ExecScreen'
import { KpiScreen } from '@/features/exec/KpiScreen'
import { ReviewScreen } from '@/features/exec/ReviewScreen'
import { ForecastScreen } from '@/features/exec/ForecastScreen'
import { InsightsScreen } from '@/features/exec/InsightsScreen'
import { BoardScreen } from '@/features/exec/BoardScreen'
import { SalesPerformanceScreen } from '@/features/exec/SalesPerformanceScreen'
import { DealPerformanceScreen } from '@/features/exec/DealPerformanceScreen'
import { OrgScreen } from '@/features/exec/OrgScreen'
import { PortfolioScreen } from '@/features/planning/PortfolioScreen'
import { AccountPlanScreen } from '@/features/planning/AccountPlanScreen'
import { OpportunityPlanScreen } from '@/features/planning/OpportunityPlanScreen'
import { LeadPlanScreen } from '@/features/planning/LeadPlanScreen'
import { StrategyScreen } from '@/features/planning/StrategyScreen'
import { OperationsScreen } from '@/features/planning/OperationsScreen'
import { ReportsScreen } from '@/features/analytics/ReportsScreen'
import { CampaignScreen } from '@/features/analytics/CampaignScreen'
import { SearchScreen } from '@/features/search/SearchScreen'
import {
  ApprovalsSetupScreen,
  DataQualityScreen,
  FieldsScreen,
  FlowsScreen,
  LayoutScreen,
  ListViewScreen,
  ObjectsScreen,
  OnboardingScreen,
  PermissionsScreen,
  SchemaScreen,
  SetupHomeScreen,
  StagesScreen,
  ValidationScreen,
} from '@/features/setup'

/**
 * Every screen, as a route.
 *
 * CODE-BASED AND NOT FILE-BASED, DELIBERATELY. The file-based router needs a build plugin and a
 * generated tree; this application has forty-odd flat screens and no nested loaders, so the tree
 * below is shorter than the configuration the generator would need — and it is the one place a
 * reader can see the whole application at once.
 *
 * THE SHELL IS THE ROOT. Everything renders inside it, so navigating never remounts the rail or
 * loses a list's scroll position.
 */

const rootRoute = createRootRoute({ component: AppShell })

/**
 * Generic over the path so the literal survives into the router's type. A `path: string`
 * parameter here would widen every route to `string` and take `<Link to>` checking with it —
 * which is the whole reason this router was chosen.
 */
const route = <Path extends string>(path: Path, component: () => React.JSX.Element) =>
  createRoute({ getParentRoute: () => rootRoute, path, component })

// ── sales
const indexRoute = createRoute({ getParentRoute: () => rootRoute, path: '/', component: ConsoleScreen })

const listRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/records/$object',
  component: function List() {
    const { object } = listRoute.useParams()
    return <ListScreen objectKey={object} />
  },
})

const recordRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/records/$object/$id',
  component: function Record() {
    const { object, id } = recordRoute.useParams()
    return <RecordScreen objectKey={object} id={id} />
  },
})

const quoteRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/quote/$id',
  component: function Quote() {
    const { id } = quoteRoute.useParams()
    return <QuoteBuilderScreen quoteId={id} />
  },
})

export const routeTree = rootRoute.addChildren([
  indexRoute,
  route('/kanban', KanbanScreen),
  listRoute,
  recordRoute,
  quoteRoute,

  // ── service
  route('/service/cases', CaseConsoleScreen),
  route('/service/board', CaseBoardScreen),
  route('/service/sla', SlaScreen),

  // ── my work
  route('/work/inbox', InboxScreen),
  route('/work/calendar', ActivityScreen),
  route('/work/mobile', MobileScreen),

  // ── executive
  route('/exec', ExecScreen),
  route('/exec/board', BoardScreen),
  route('/exec/kpis', KpiScreen),
  route('/exec/reviews', ReviewScreen),
  route('/exec/forecast', ForecastScreen),
  route('/exec/insights', InsightsScreen),
  route('/exec/sales-performance', SalesPerformanceScreen),
  route('/exec/deal-performance', DealPerformanceScreen),
  route('/exec/org', OrgScreen),

  // ── planning
  route('/plan/portfolio', PortfolioScreen),
  route('/plan/accounts', AccountPlanScreen),
  route('/plan/opportunities', OpportunityPlanScreen),
  route('/plan/leads', LeadPlanScreen),
  route('/plan/strategy', StrategyScreen),
  route('/plan/operations', OperationsScreen),

  // ── analytics
  route('/analytics/reports', ReportsScreen),
  route('/analytics/campaigns', CampaignScreen),

  // ── search
  route('/search', SearchScreen),

  // ── setup
  route('/setup', SetupHomeScreen),
  route('/setup/objects', ObjectsScreen),
  route('/setup/fields', FieldsScreen),
  route('/setup/layout', LayoutScreen),
  route('/setup/list-views', ListViewScreen),
  route('/setup/stages', StagesScreen),
  route('/setup/schema', SchemaScreen),
  route('/setup/permissions', PermissionsScreen),
  route('/setup/flows', FlowsScreen),
  route('/setup/approvals', ApprovalsSetupScreen),
  route('/setup/validation', ValidationScreen),
  route('/setup/quality', DataQualityScreen),
  route('/setup/onboarding', OnboardingScreen),
])

export const router = createRouter({
  routeTree,
  defaultPreload: 'intent',

  // Without this the router draws its own `<p>Not Found</p>` — see `NotFound` for why that is the
  // wrong sentence in this application. Set on the router rather than on the root route so a
  // `notFound()` thrown from anywhere lands on the same screen.
  defaultNotFoundComponent: NotFound,
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
